using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Volt.Compiler;

/// <summary>
/// Volt source generator.
/// 1. Folder routing: every Pages/**/*.cs becomes a route registration (route derived from the file path).
/// 2. Islands: [VoltIsland] components get generated typed state serialization (Utf8JsonWriter/JsonDocument,
///    reflection-free, Native AOT safe), action dispatch and fragment rendering.
/// Registration happens via [ModuleInitializer] — no reflection at runtime.
/// </summary>
[Generator]
public sealed class VoltGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        RegisterRoutesPipeline(context);
        RegisterIslandsPipeline(context);
        RegisterVoltFilePipeline(context);
    }

    // ==================================================================
    // Pipeline 3: .volt template pages (Razor-like markup + C#)
    // ==================================================================

    private static void RegisterVoltFilePipeline(IncrementalGeneratorInitializationContext context)
    {
        // raw texts: parsing happens in the emit step — .volt files need cross-file
        // knowledge (partial signatures, layout graph) that a per-file transform can't see
        var files = context.AdditionalTextsProvider
            .Where(file =>
            {
                var path = file.Path.Replace('\\', '/');
                return path.EndsWith(".volt", StringComparison.OrdinalIgnoreCase);
            })
            .Select((file, ct) => (Path: file.Path, Text: file.GetText(ct)?.ToString() ?? ""))
            .WithTrackingName("VoltFileTexts")
            .Collect();

        context.RegisterSourceOutput(files, (spc, texts) => EmitVoltFiles(spc, texts));
    }

    // ------------------------------------------------------------------
    // .volt emission: three passes over the collected texts
    //   A. header scan → partial signatures + layout classification
    //   B. full parse (partials table available for bodies)
    //   C. layout graph resolution + compile-time splice into pages
    // ------------------------------------------------------------------

    private sealed class HeaderScan
    {
        public bool IsLayout;
        public string LayoutOverride = "";
        public VoltFileParser.PartialSignature? Partial;
    }

    private static HeaderScan ScanHeader(string path, string text)
    {
        var scan = new HeaderScan { IsLayout = VoltFileParser.IsLayoutFile(path) };
        int i = 0;
        while (true)
        {
            int lineEnd = text.IndexOf('\n', i);
            var line = (lineEnd < 0 ? text.Substring(i) : text.Substring(i, lineEnd - i)).Trim();

            if (line.StartsWith("@layout ", StringComparison.Ordinal))
                scan.LayoutOverride = line.Substring("@layout ".Length).Trim().Trim('"', '\'');
            else if (line.StartsWith("@partial ", StringComparison.Ordinal))
            {
                var decl = line.Substring("@partial ".Length).Trim();
                int open = decl.IndexOf('(');
                string name = (open < 0 ? decl : decl.Substring(0, open)).Trim();
                string type = "", pname = "";
                if (open >= 0 && decl.EndsWith(")"))
                {
                    var param = decl.Substring(open + 1, decl.Length - open - 2).Trim();
                    int space = param.LastIndexOf(' ');
                    if (space > 0)
                    {
                        type = param.Substring(0, space).Trim();
                        pname = param.Substring(space + 1).Trim();
                    }
                }
                scan.Partial = new VoltFileParser.PartialSignature(name, type, pname);
            }
            else if (line.Length > 0 && !line.StartsWith("@", StringComparison.Ordinal))
                break; // first content line: body starts here

            if (lineEnd < 0) break;
            i = lineEnd + 1;
        }
        return scan;
    }

    /// <summary>Directory (with trailing '/', normalized) that a .volt path lives in.</summary>
    private static string DirOf(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized.Substring(0, slash + 1);
    }

    /// <summary>Layout name from the file stem: _Layout.volt → "", _AdminLayout.volt → "Admin".</summary>
    private static string LayoutNameOf(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (stem.Length == 7) return ""; // _Layout
        return stem.Substring(1, stem.Length - 1 - 6); // _<Name>Layout → <Name>
    }

    private sealed record LayoutEntry(string FilePath, string Dir, string Name, string Before, string After);

    private static void EmitVoltFiles(SourceProductionContext spc, ImmutableArray<(string Path, string Text)> texts)
    {
        // ---- pass A: headers (partial signatures, layout overrides) -------------------
        var partials = new Dictionary<string, VoltFileParser.PartialSignature>(StringComparer.Ordinal);
        foreach (var (path, text) in texts)
        {
            var scan = ScanHeader(path, text);
            if (scan.Partial is { } sig)
            {
                if (partials.TryGetValue(sig.Name, out var dup) && !string.Equals(dup.ParamType, sig.ParamType, StringComparison.Ordinal))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor("VOLT023", "Volt partial", "duplicate partial name '{0}' ({1})", "Volt", DiagnosticSeverity.Error, true),
                        Location.None, sig.Name, Path.GetFileName(path)));
                    continue;
                }
                partials[sig.Name] = sig;
            }
        }

        // ---- pass B: full parse with the partials table -------------------------------
        var models = new List<VoltFileParser.VoltFileModel>(texts.Length);
        foreach (var (path, text) in texts)
            models.Add(VoltFileParser.Parse(path, text, partials));

        // ---- layout index: (dir, lowercase name) → model --------------------------------
        var layoutsByDir = new Dictionary<(string Dir, string Name), VoltFileParser.VoltFileModel>();
        foreach (var model in models)
        {
            if (!model.IsLayout || model.Diagnostic.Length > 0) continue;
            var key = (DirOf(model.FilePath), LayoutNameOf(model.FilePath).ToLowerInvariant());
            if (layoutsByDir.ContainsKey(key))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("VOLT021", "Volt layout", "duplicate layout '{0}' in {1}", "Volt", DiagnosticSeverity.Error, true),
                    Location.None, key.Item2.Length == 0 ? "_Layout" : key.Item2, key.Item1));
                continue;
            }
            layoutsByDir[key] = model;
        }

        // resolve each layout's final splice (nested layouts), memoized
        var resolvedLayouts = new Dictionary<string, LayoutEntry>();
        LayoutEntry? ResolveLayout(VoltFileParser.VoltFileModel layout, HashSet<string> visiting)
        {
            if (resolvedLayouts.TryGetValue(layout.FilePath, out var cached)) return cached;
            if (!visiting.Add(layout.FilePath))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("VOLT021", "Volt layout", "layout cycle involving '{0}'", "Volt", DiagnosticSeverity.Error, true),
                    Location.None, Path.GetFileName(layout.FilePath)));
                return null;
            }

            string before = layout.BodyBefore, after = layout.BodyAfter;
            if (TryFindLayout(DirOf(layout.FilePath), layout.LayoutOverride, layout.FilePath, out var parent)
                && parent is { } parentModel)
            {
                if (ResolveLayout(parentModel, visiting) is { } parentResolved)
                {
                    before = parentResolved.Before + before;
                    after = after + parentResolved.After;
                }
                else return null;
            }

            visiting.Remove(layout.FilePath);
            var entry = new LayoutEntry(layout.FilePath, DirOf(layout.FilePath), LayoutNameOf(layout.FilePath), before, after);
            resolvedLayouts[layout.FilePath] = entry;
            return entry;
        }

        bool TryFindLayout(string startDir, string requested, string? excludeFilePath, out VoltFileParser.VoltFileModel? found)
        {
            found = null;
            if (requested.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
            var name = requested.ToLowerInvariant(); // "" = default _Layout.volt
            var dir = startDir;
            while (dir.Length > 0)
            {
                if (layoutsByDir.TryGetValue((dir, name), out var model) && model.FilePath != excludeFilePath)
                {
                    found = model;
                    return true;
                }
                int slash = dir.Length >= 2 ? dir.LastIndexOf('/', dir.Length - 2) : -1;
                dir = slash < 0 ? "" : dir.Substring(0, slash + 1);
            }
            return false;
        }

        foreach (var model in models)
        {
            if (model.IsLayout && model.Diagnostic.Length == 0)
                ResolveLayout(model, new HashSet<string>(StringComparer.Ordinal));
        }

        // ---- pass C: emit pages (spliced) + partials ----------------------------------
        var sb = new StringBuilder(1024);
        sb.AppendLine("// <auto-generated> Volt .volt page registration — do not edit");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using Volt;");
        sb.AppendLine();
        sb.AppendLine("namespace Volt.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class VoltFileInit");
        sb.AppendLine("    {");
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        internal static void Init()");
        sb.AppendLine("        {");

        var partialSb = new StringBuilder(1024);
        partialSb.AppendLine("// <auto-generated> Volt .volt partials — do not edit");
        partialSb.AppendLine("#nullable enable");
        partialSb.AppendLine("using System;");
        partialSb.AppendLine("using Volt;");
        partialSb.AppendLine();
        partialSb.AppendLine("namespace Volt.Generated");
        partialSb.AppendLine("{");
        partialSb.AppendLine("    internal static class VoltPartials");
        partialSb.AppendLine("    {");
        bool anyPartial = false;

        foreach (var model in models)
        {
            if (model.Diagnostic.Length > 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("VOLT020", "Volt .volt page", "{0} ({1})", "Volt", DiagnosticSeverity.Error, true),
                    Location.None, model.Diagnostic, System.IO.Path.GetFileName(model.FilePath)));
                continue;
            }

            if (model.IsPartial)
            {
                anyPartial = true;
                var param = model.PartialParamType.Length > 0
                    ? $", {model.PartialParamType} {model.PartialParamName}"
                    : "";
                partialSb.AppendLine($"        public static void {model.PartialName}(HtmlWriter w, RenderContext ctx{param})");
                partialSb.AppendLine("        {");
                partialSb.Append(model.RenderBody);
                partialSb.AppendLine("        }");
                continue;
            }

            if (model.IsLayout) continue; // consumed via splice

            // resolve the page's layout (convention: nearest _Layout.volt up the tree)
            string before = "", after = "";
            VoltFileParser.VoltFileModel? layoutModel = null;
            if (model.LayoutOverride.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                // explicit opt-out
            }
            else if (TryFindLayout(DirOf(model.FilePath), model.LayoutOverride, null, out var found) && found is not null)
            {
                layoutModel = found;
            }
            else if (model.LayoutOverride.Length > 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("VOLT021", "Volt layout", "@layout '{0}' not found for {1}", "Volt", DiagnosticSeverity.Error, true),
                    Location.None, model.LayoutOverride, System.IO.Path.GetFileName(model.FilePath)));
                continue;
            }

            if (layoutModel is not null && resolvedLayouts.TryGetValue(layoutModel.FilePath, out var layoutEntry))
            {
                before = layoutEntry.Before;
                after = layoutEntry.After;
            }

            var typeRef = $"global::{model.Namespace}.{model.ClassName}";
            if (model.IsNotFound)
            {
                sb.AppendLine($"            RouteRegistry.Root.SetNotFound(static () => new {typeRef}());");
            }
            else if (model.IsError)
            {
                sb.AppendLine($"            RouteRegistry.Root.SetError(static () => new {typeRef}());");
            }
            else
            {
                sb.AppendLine($"            RouteRegistry.Root.Add({SymbolDisplay.FormatLiteral(model.Route, quote: true)}, static () => new {typeRef}());");
            }

            // page class (layout spliced around the body at compile time — zero runtime cost)
            var page = new StringBuilder(2048);
            page.AppendLine("// <auto-generated> Volt .volt page — do not edit");
            page.AppendLine("#nullable enable");
            page.AppendLine("using Volt;");
            page.AppendLine();
            page.AppendLine($"namespace {model.Namespace};");
            page.AppendLine();
            page.AppendLine($"public sealed class {model.ClassName} : VoltPage");
            page.AppendLine("{");
            page.AppendLine($"    public override RenderMode Mode => RenderMode.{model.Mode};");
            if (model.Revalidate >= 0)
                page.AppendLine($"    public override int RevalidateSeconds => {model.Revalidate};");
            page.AppendLine();
            page.AppendLine("    public override void Render(HtmlWriter w, RenderContext ctx)");
            page.AppendLine("    {");
            page.Append(before);
            page.Append(model.RenderBody);
            page.Append(after);
            page.AppendLine("    }");
            page.AppendLine("}");
            spc.AddSource($"{model.ClassName}.g.cs", SourceText.From(page.ToString(), Encoding.UTF8));
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("VoltFilesInit.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

        if (anyPartial)
        {
            partialSb.AppendLine("    }");
            partialSb.AppendLine("}");
            spc.AddSource("VoltPartials.g.cs", SourceText.From(partialSb.ToString(), Encoding.UTF8));
        }
    }

    // ==================================================================
    // Pipeline 1: folder routes from AdditionalFiles (VoltItem=Page)
    // ==================================================================

    private static void RegisterRoutesPipeline(IncrementalGeneratorInitializationContext context)
    {
        var projectDir = context.AnalyzerConfigOptionsProvider
            .Select((options, _) => options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var dir) ? dir : "");

        var optionsProvider = context.AnalyzerConfigOptionsProvider.Select((o, _) => o);

        var pages = context.AdditionalTextsProvider
            .Combine(optionsProvider)
            .Where(pair =>
            {
                var (file, provider) = pair;
                var fileOptions = provider.GetOptions(file);
                if (fileOptions.TryGetValue("build_metadata.AdditionalFiles.VoltItem", out var item)
                    && item == "Page")
                    return true;
                // convention fallback: any AdditionalText under a Pages/ directory is a page
                var path = file.Path.Replace('\\', '/');
                return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                       && path.Contains("/Pages/", StringComparison.OrdinalIgnoreCase);
            })
            .Combine(projectDir)
            .Select((pair, ct) =>
            {
                var ((file, _), dir) = pair;
                return PageModel.From(file.Path, file.GetText(ct)?.ToString() ?? "", dir);
            })
            .WithTrackingName("VoltPages")
            .Collect();

        context.RegisterSourceOutput(pages, (spc, pages) => EmitRoutes(spc, pages));
    }

    // ------------------------------------------------------------------
    // Route model (pure data; cacheable)
    // ------------------------------------------------------------------

    private enum PageKind { Route, NotFound, Error }

    private readonly record struct PageModel(
        string FilePath,
        string Route,
        PageKind Kind,
        string Namespace,
        string TypeName,
        string Diagnostic)
    {
        public static PageModel From(string filePath, string text, string projectDir)
        {
            var model = ParseType(filePath, text);
            var route = RouteFromPath(filePath, projectDir, out var kind);
            return model with { Route = route, Kind = kind };
        }

        private static PageModel ParseType(string filePath, string text)
        {
            try
            {
                var tree = CSharpSyntaxTree.ParseText(text);
                var root = tree.GetCompilationUnitRoot();

                string ns = "";
                var nsDecl = (SyntaxNode?)root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault()
                             ?? root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
                if (nsDecl is FileScopedNamespaceDeclarationSyntax fileNs) ns = fileNs.Name.ToString().Trim();
                else if (nsDecl is NamespaceDeclarationSyntax blockNs) ns = blockNs.Name.ToString().Trim();

                TypeDeclarationSyntax? type = null;
                foreach (var member in root.Members)
                {
                    if (member is TypeDeclarationSyntax topLevelType)
                    {
                        type = topLevelType;
                        break;
                    }
                    if (member is BaseNamespaceDeclarationSyntax nsMember)
                    {
                        foreach (var inner in nsMember.Members)
                        {
                            if (inner is TypeDeclarationSyntax innerType)
                            {
                                type = innerType;
                                break;
                            }
                        }
                        if (type is not null) break;
                    }
                }

                if (type is null)
                    return new PageModel(filePath, "", PageKind.Route, "", "", "VOLT001: no top-level type found in page file");

                string baseList = "";
                var b = type.BaseList;
                while (b is not null)
                {
                    baseList = b.Types.ToString();
                    break;
                }
                if (baseList.Contains("VoltPage") == false && baseList.Contains("VoltErrorPage") == false && baseList.Length > 0)
                    return new PageModel(filePath, "", PageKind.Route, ns, type.Identifier.ValueText,
                        "VOLT003: page types must derive from VoltPage (shared helpers belong outside Pages/)");

                return new PageModel(filePath, "", PageKind.Route, ns, type.Identifier.ValueText, "");
            }
            catch (Exception ex)
            {
                return new PageModel(filePath, "", PageKind.Route, "", "", $"VOLT001: page file could not be parsed ({ex.Message})");
            }
        }

        private static string RouteFromPath(string filePath, string projectDir, out PageKind kind)
        {
            kind = PageKind.Route;
            var rel = filePath;
            if (projectDir.Length > 0 && filePath.StartsWith(projectDir))
                rel = filePath.Substring(projectDir.Length).TrimStart('/', '\\');

            var normalized = rel.Replace('\\', '/');
            const string pagesPrefix = "Pages/";
            int pagesAt = normalized.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
            if (pagesAt >= 0)
                normalized = normalized.Substring(pagesAt + "/Pages/".Length); // convention: any Pages/ dir
            else if (normalized.StartsWith(pagesPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(pagesPrefix.Length);

            var dot = normalized.LastIndexOf('.');
            if (dot >= 0) normalized = normalized.Substring(0, dot);

            var segments = SplitSkipEmpty(normalized, '/');
            if (segments.Count == 0) return "/";

            var last = segments[segments.Count - 1];
            if (segments.Count == 1 && last.Equals("notfound", StringComparison.OrdinalIgnoreCase))
            {
                kind = PageKind.NotFound;
                return "";
            }
            if (segments.Count == 1 && last.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                kind = PageKind.Error;
                return "";
            }

            if (last.Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Count == 1) return "/";
                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments[segments.Count - 1] = SegmentToRoute(last);
            }

            for (int i = 0; i < segments.Count - 1; i++)
                segments[i] = SegmentToRoute(segments[i]);

            // static segments are lowercased (About.cs -> /about); param names keep their case
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].StartsWith("{", StringComparison.Ordinal)) continue;
                segments[i] = segments[i].ToLowerInvariant();
            }

            return "/" + string.Join("/", segments);
        }

        private static string SegmentToRoute(string segment)
        {
            // [slug] → {slug}, [...rest] → {*rest}
            if (segment.StartsWith("[...", StringComparison.Ordinal) && segment.EndsWith("]") && segment.Length > 5)
                return "{*" + segment.Substring(4, segment.Length - 5) + "}";
            if (segment.StartsWith("[", StringComparison.Ordinal) && segment.EndsWith("]") && segment.Length > 2)
                return "{" + segment.Substring(1, segment.Length - 2) + "}";
            return segment;
        }

        internal static List<string> SplitSkipEmpty(string s, char sep)
        {
            var result = new List<string>();
            int start = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                if (i == s.Length || s[i] == sep)
                {
                    if (i > start) result.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            return result;
        }
    }

    private static void EmitRoutes(SourceProductionContext spc, ImmutableArray<PageModel> pages)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("// <auto-generated> Volt folder routes — do not edit");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using Volt;");
        sb.AppendLine();
        sb.AppendLine("namespace Volt.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class VoltRouteInit");
        sb.AppendLine("    {");
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        internal static void Init()");
        sb.AppendLine("        {");

        var seenRoutes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (page.Diagnostic.Length > 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("VOLT001", "Volt page", "{0} ({1})", "Volt", DiagnosticSeverity.Error, true),
                    Location.None, page.Diagnostic, Path.GetFileName(page.FilePath)));
                continue;
            }

            var ctor = $"new global::{(page.Namespace.Length > 0 ? page.Namespace + "." : "")}{page.TypeName}()";
            switch (page.Kind)
            {
                case PageKind.NotFound:
                    sb.AppendLine($"            RouteRegistry.Root.SetNotFound(static () => {ctor});");
                    break;
                case PageKind.Error:
                    sb.AppendLine($"            RouteRegistry.Root.SetError(static () => {ctor});");
                    break;
                default:
                    if (!seenRoutes.Add(page.Route))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            new DiagnosticDescriptor("VOLT002", "Volt route conflict", "duplicate route '{0}' ({1})", "Volt", DiagnosticSeverity.Error, true),
                            Location.None, page.Route, Path.GetFileName(page.FilePath)));
                        continue;
                    }
                    sb.AppendLine($"            RouteRegistry.Root.Add({SymbolDisplay.FormatLiteral(page.Route, quote: true)}, static () => {ctor});");
                    break;
            }
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("VoltRoutes.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    // ==================================================================
    // Pipeline 2: islands from [VoltIsland] symbols
    // ==================================================================

    private readonly record struct ActionModel(string MethodName, string ActionName, bool HasArgs);

    private enum PropKind { Int32, Int64, Boolean, Double, Single, Decimal, String, Other }

    private readonly record struct PropModel(
        string JsonName,      // camelCase json name
        string CodeName,      // C# property/ctor-param name
        string TypeFqn,       // global::-qualified
        PropKind Kind,
        bool IsNullable,
        string? CtorParamName) // matched ctor parameter or null
    {
        public string EncodedFieldName => "V_" + JsonName.Replace("-", "_");
    }

    private readonly record struct IslandModel(
        string Namespace,
        string ClassName,
        string StateTypeFqn,
        string StateTypeName,
        bool IsPartial,
        bool IsDerivedCorrectly,
        ImmutableArray<ActionModel> Actions,
        ImmutableArray<PropModel> Properties,
        bool UseCtorInit,          // true: new T(args); false: new T { props }
        string? Wasm,              // [VoltIsland(Wasm = "…")] — client-side dispatch module URL
        ImmutableArray<string> Diagnostics)
    {
        public string TypeFqn => (Namespace.Length > 0 ? Namespace + "." : "") + ClassName;
    }

    private static void RegisterIslandsPipeline(IncrementalGeneratorInitializationContext context)
    {
        var islands = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Volt.VoltIslandAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => FromSymbol((INamedTypeSymbol)ctx.TargetSymbol, ctx.TargetNode as TypeDeclarationSyntax))
            .WithTrackingName("VoltIslands")
            .Collect();

        context.RegisterSourceOutput(islands, (spc, islands_) => EmitIslands(spc, islands_));
    }

    private static IslandModel FromSymbol(INamedTypeSymbol symbol, TypeDeclarationSyntax? node)
    {
        var diagnostics = ImmutableArray.CreateBuilder<string>();

        bool isPartial = node?.Modifiers.Any(SyntaxKind.PartialKeyword) ?? false;
        if (!isPartial)
            diagnostics.Add($"island '{symbol.Name}' must be declared partial (source generation extends it)");

        if (symbol.IsGenericType)
            diagnostics.Add($"island '{symbol.Name}' must not be generic");

        INamedTypeSymbol? baseType = symbol.BaseType;
        INamedTypeSymbol? componentBase = null;
        while (baseType is not null)
        {
            var od = baseType.OriginalDefinition;
            if (od.Arity == 1 && od.Name == "VoltComponent" && od.ContainingNamespace?.ToDisplayString() == "Volt")
            {
                componentBase = baseType;
                break;
            }
            baseType = baseType.BaseType;
        }
        if (componentBase is null || componentBase.TypeArguments.Length != 1)
        {
            diagnostics.Add($"island '{symbol.Name}' must derive from VoltComponent<TState>");
            return new IslandModel(
                symbol.ContainingNamespace?.ToDisplayString() ?? "",
                symbol.Name, "", "", isPartial, false,
                ImmutableArray<ActionModel>.Empty, ImmutableArray<PropModel>.Empty, false, null,
                diagnostics.ToImmutable());
        }

        var stateType = (INamedTypeSymbol)componentBase.TypeArguments[0];

        string? wasm = null;
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != "Volt.VoltIslandAttribute") continue;
            foreach (var arg in attr.NamedArguments)
            {
                if (arg.Key == "Wasm" && arg.Value.Value is string wasmUrl && wasmUrl.Length > 0)
                    wasm = wasmUrl;
            }
        }

        var properties = CollectProperties(stateType, diagnostics, symbol.Name);
        bool useCtorInit = properties.Length > 0 && properties.All(p => p.CtorParamName is not null);

        var actions = ImmutableArray.CreateBuilder<ActionModel>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;
            if (!method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Volt.VoltActionAttribute")) continue;

            string actionName = NormalizeActionName(method.Name);
            bool hasArgs = method.Parameters.Length == 2;
            actions.Add(new ActionModel(method.Name, actionName, hasArgs));

            if (!method.IsStatic)
                diagnostics.Add($"action '{method.Name}' on island '{symbol.Name}' must be static");
            if (method.ReturnType.ToDisplayString() != stateType.ToDisplayString())
                diagnostics.Add($"action '{method.Name}' on island '{symbol.Name}' must return {stateType.Name}");
            if (method.Parameters.Length < 1 || method.Parameters.Length > 2
                || method.Parameters[0].Type.ToDisplayString() != stateType.ToDisplayString()
                || (method.Parameters.Length == 2 && method.Parameters[1].Type.ToDisplayString() != "System.Text.Json.JsonElement"))
                diagnostics.Add($"action '{method.Name}' on island '{symbol.Name}' must be (TState state) or (TState state, JsonElement args)");
        }

        return new IslandModel(
            symbol.ContainingNamespace?.ToDisplayString() ?? "",
            symbol.Name,
            stateType.ToDisplayString(),
            stateType.Name,
            isPartial, true,
            actions.ToImmutable(), properties, useCtorInit, wasm,
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<PropModel> CollectProperties(
        INamedTypeSymbol stateType, ImmutableArray<string>.Builder diagnostics, string islandName)
    {
        var props = ImmutableArray.CreateBuilder<PropModel>();
        foreach (var member in stateType.GetMembers())
        {
            if (member is not IPropertySymbol property) continue;
            if (property.DeclaredAccessibility != Accessibility.Public) continue;
            if (property.IsStatic) continue;
            if (property.GetMethod is null || property.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
            if (property.Parameters.Length > 0) continue; // indexer

            var (kind, isNullable) = Classify(property.Type);
            if (kind == PropKind.Other)
            {
                diagnostics.Add(
                    $"state type '{stateType.Name}' of island '{islandName}': property '{property.Name}' has unsupported type '{property.Type.ToDisplayString()}' " +
                    "(supported: int, long, bool, double, float, decimal, string, and their nullable forms)");
                continue;
            }
            props.Add(new PropModel(
                JsonName: ToCamelCase(property.Name),
                CodeName: property.Name,
                TypeFqn: property.Type.ToDisplayString().StartsWith("global::", StringComparison.Ordinal)
                    ? property.Type.ToDisplayString()
                    : "global::" + property.Type.ToDisplayString(),
                Kind: kind,
                IsNullable: isNullable,
                CtorParamName: null));
        }

        // match ctor parameters by name (positional records)
        bool hasParamCtor = false;
        foreach (var ctor in stateType.InstanceConstructors)
        {
            if (ctor.Parameters.Length == 0) continue;
            if (ctor.Parameters.Length != props.Count) continue;
            bool allMatch = true;
            foreach (var param in ctor.Parameters)
            {
                var propIndex = -1;
                for (int i = 0; i < props.Count; i++)
                {
                    if (props[i].CodeName.Equals(param.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        propIndex = i;
                        break;
                    }
                }
                if (propIndex < 0 || !TypeMatches(props[propIndex], param.Type))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
            {
                hasParamCtor = true;
                foreach (var param in ctor.Parameters)
                {
                    for (int i = 0; i < props.Count; i++)
                    {
                        if (props[i].CodeName.Equals(param.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            props[i] = props[i] with { CtorParamName = param.Name };
                            break;
                        }
                    }
                }
                break;
            }
        }

        if (!hasParamCtor && props.Count > 0)
        {
            // need settable/init properties for object-initializer deserialization
            foreach (var prop in props)
            {
                var property = stateType.GetMembers().OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.Name == prop.CodeName);
                if (property is null || property.SetMethod is null || property.SetMethod.DeclaredAccessibility != Accessibility.Public)
                {
                    diagnostics.Add(
                        $"state type '{stateType.Name}' of island '{islandName}': property '{prop.CodeName}' must have a public set/init " +
                        "or match a constructor parameter (positional record)");
                    break;
                }
            }
        }

        return props.ToImmutable();
    }

    private static bool TypeMatches(PropModel prop, ITypeSymbol type)
        => type.ToDisplayString().Equals(prop.TypeFqn, StringComparison.Ordinal)
           || ("global::" + type.ToDisplayString()).Equals(prop.TypeFqn, StringComparison.Ordinal);

    private static (PropKind Kind, bool IsNullable) Classify(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            var (kind, _) = Classify(named.TypeArguments[0]);
            return (kind, true);
        }
        return type.SpecialType switch
        {
            SpecialType.System_Int32 => (PropKind.Int32, false),
            SpecialType.System_Int64 => (PropKind.Int64, false),
            SpecialType.System_Boolean => (PropKind.Boolean, false),
            SpecialType.System_Double => (PropKind.Double, false),
            SpecialType.System_Single => (PropKind.Single, false),
            SpecialType.System_Decimal => (PropKind.Decimal, false),
            SpecialType.System_String => (PropKind.String, false),
            _ => (PropKind.Other, false),
        };
    }

    private static string ToCamelCase(string name)
    {
        if (name.Length == 0) return name;
        var chars = name.ToCharArray();
        int i = 0;
        while (i < chars.Length && char.IsUpper(chars[i])) i++;
        if (i == 0) return name;
        if (i == 1) { chars[0] = char.ToLowerInvariant(chars[0]); return new string(chars); }
        if (i == chars.Length) return name.ToLowerInvariant();
        // multiple leading capitals: lower only the first (URLStyle → urlStyle)
        chars[0] = char.ToLowerInvariant(chars[0]);
        return new string(chars);
    }

    private static string NormalizeActionName(string methodName)
    {
        var name = methodName;
        if (name.StartsWith("On", StringComparison.Ordinal) && name.Length > 2) name = name.Substring(2);
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5) name = name.Substring(0, name.Length - 5);
        return name.ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // island emission
    // ------------------------------------------------------------------

    private static void EmitIslands(SourceProductionContext spc, ImmutableArray<IslandModel> islands)
    {
        if (islands.Length == 0) return;

        var sb = new StringBuilder(4096);
        sb.AppendLine("// <auto-generated> Volt islands — do not edit");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using Volt;");
        sb.AppendLine();
        sb.AppendLine("namespace Volt.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class VoltIslandInit");
        sb.AppendLine("    {");
        sb.AppendLine("        [ModuleInitializer]");
        sb.AppendLine("        internal static void Init()");
        sb.AppendLine("        {");

        int valid = 0;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var perIsland = new List<(string ClassName, string Text)>();

        foreach (var island in islands)
        {
            if (island.Diagnostics.Length > 0 || !island.IsDerivedCorrectly)
            {
                foreach (var d in island.Diagnostics)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor("VOLT010", "Volt island", "{0}", "Volt", DiagnosticSeverity.Error, true),
                        Location.None, d));
                }
                continue;
            }
            if (!island.IsPartial) continue; // already reported
            if (!seenNames.Add(island.ClassName))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor("VOLT011", "Volt island", "duplicate island name '{0}'", "Volt", DiagnosticSeverity.Error, true),
                    Location.None, island.ClassName));
                continue;
            }

            valid++;
            string fqn = "global::" + island.TypeFqn;
            string stateFqn = island.StateTypeFqn.StartsWith("global::", StringComparison.Ordinal)
                ? island.StateTypeFqn
                : "global::" + island.StateTypeFqn;
            string safeName = SafeContextName(island.TypeFqn);

            // registration (into the single Init method)
            sb.AppendLine($"            VoltIslandRegistry.Root.Register(new VoltIslandEntry");
            sb.AppendLine($"            {{");
            sb.AppendLine($"                Name = {SymbolDisplay.FormatLiteral(island.ClassName, quote: true)},");
            sb.AppendLine($"                Dispatch = static (stateJson, action, argsJson) => VoltIslands_{safeName}.Dispatch(stateJson, action, argsJson),");
            sb.AppendLine($"                Render = static (stateJson, sid, token) => VoltIslands_{safeName}.Render(stateJson, sid, token),");
            if (island.Wasm is { Length: > 0 } wasmUrl)
                sb.AppendLine($"                WasmModule = {SymbolDisplay.FormatLiteral(wasmUrl, quote: true)},");
            sb.AppendLine($"            }});");

            // dispatch + fragment render + partial impl, one file per island
            perIsland.Add((safeName, EmitIslandCode(island, fqn, stateFqn, safeName)));
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        if (valid > 0)
        {
            spc.AddSource("VoltIslands.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
            foreach (var (name, text) in perIsland)
            {
                spc.AddSource($"VoltIslands_{name}.g.cs", SourceText.From(text, Encoding.UTF8));
            }
        }
    }

    private static string SafeContextName(string typeFqn)
    {
        var sb = new StringBuilder(typeFqn.Length);
        foreach (char c in typeFqn)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else if (c == '.' || c == '+') sb.Append('_');
            // generic commas/angle brackets dropped
        }
        return sb.ToString();
    }

    private static string EmitIslandCode(IslandModel island, string fqn, string stateFqn, string safeName)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("// <auto-generated> Volt island — do not edit");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using Volt;");
        sb.AppendLine();
        sb.AppendLine("namespace Volt.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal static class VoltIslands_{safeName}");
        sb.AppendLine("    {");
        sb.AppendLine("        public static byte[] Dispatch(ReadOnlySpan<byte> stateJson, string action, ReadOnlySpan<byte> argsJson)");
        sb.AppendLine("        {");
        if (island.Actions.Length == 0)
        {
            sb.AppendLine($"            throw new VoltActionNotFoundException({SymbolDisplay.FormatLiteral(island.ClassName, quote: true)}, action);");
        }
        else
        {
            sb.AppendLine("            switch (action)");
            sb.AppendLine("            {");
            foreach (var act in island.Actions)
            {
                sb.AppendLine($"                case {SymbolDisplay.FormatLiteral(act.ActionName, quote: true)}:");
                sb.AppendLine("                {");
                sb.AppendLine($"                    var state = {fqn}.DeserializeState(stateJson);");
                if (act.HasArgs)
                    sb.AppendLine("                    var args = JsonDocument.Parse(new ReadOnlySequence<byte>(argsJson.ToArray())).RootElement;");
                sb.AppendLine($"                    return {fqn}.SerializeState({fqn}.{act.MethodName}(state{(act.HasArgs ? ", args" : "")}));");
                sb.AppendLine("                }");
            }
            sb.AppendLine("                default:");
            sb.AppendLine($"                    throw new VoltActionNotFoundException({SymbolDisplay.FormatLiteral(island.ClassName, quote: true)}, action);");
            sb.AppendLine("            }");
        }
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public static VoltFragment Render(ReadOnlySpan<byte> stateJson, string sid, string? token)");
        sb.AppendLine("        {");
        sb.AppendLine($"            var state = {fqn}.DeserializeState(stateJson);");
        sb.AppendLine("            var fragment = new VoltFragment();");
        sb.AppendLine("            var w = fragment.Writer;");
        sb.AppendLine("            var ctx = RenderContext.ForFragment();");
        sb.AppendLine("            ctx.FallbackStore = VoltRuntime.FallbackStore;");
        sb.AppendLine("            ctx.FallbackToken = token;");
        sb.AppendLine($"            w.Island<{fqn}, {stateFqn}>(state, ctx, sid);");
        sb.AppendLine("            return fragment;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"namespace {(island.Namespace.Length > 0 ? island.Namespace : "global")}");
        sb.AppendLine("{");
        // nested classes: re-emit containing declarations
        sb.AppendLine($"    partial class {NestedClassName(island.Namespace, island.TypeFqn)} : IVoltIsland<{stateFqn}>");
        sb.AppendLine("    {");
        sb.AppendLine($"        public static string IslandName => {SymbolDisplay.FormatLiteral(island.ClassName, quote: true)};");
        sb.AppendLine($"        public static string? WasmModule => {(island.Wasm is { Length: > 0 } wasmUrl ? SymbolDisplay.FormatLiteral(wasmUrl, quote: true) : "null")};");

        // ---- serialize ----
        foreach (var prop in island.Properties)
        {
            sb.AppendLine($"        private static readonly JsonEncodedText {prop.EncodedFieldName} = JsonEncodedText.Encode({SymbolDisplay.FormatLiteral(prop.JsonName, quote: true)});");
        }
        sb.AppendLine($"        public static byte[] SerializeState({stateFqn} state)");
        sb.AppendLine("        {");
        if (island.Properties.Length == 0)
        {
            sb.AppendLine("            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state);");
        }
        else
        {
            sb.AppendLine("            var buffer = new Volt.PooledBufferWriter();");
            sb.AppendLine("            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))");
            sb.AppendLine("            {");
            sb.AppendLine("                writer.WriteStartObject();");
            foreach (var prop in island.Properties)
            {
                string value = $"state.{prop.CodeName}";
                if (prop.IsNullable && prop.Kind != PropKind.String)
                    sb.AppendLine($"                if ({value} is not null) writer.Write{WriteMethod(prop)}({prop.EncodedFieldName}, {value}.Value);");
                else
                    sb.AppendLine($"                writer.Write{WriteMethod(prop)}({prop.EncodedFieldName}, {value});");
            }
            sb.AppendLine("                writer.WriteEndObject();");
            sb.AppendLine("                writer.Flush();");
            sb.AppendLine("            }");
            sb.AppendLine("            return buffer.WrittenSpan.ToArray();");
        }
        sb.AppendLine("        }");

        // ---- deserialize ----
        sb.AppendLine($"        public static {stateFqn} DeserializeState(ReadOnlySpan<byte> utf8Json)");
        sb.AppendLine("        {");
        if (island.Properties.Length == 0)
        {
            sb.AppendLine($"            return System.Text.Json.JsonSerializer.Deserialize<{stateFqn}>(new ReadOnlySequence<byte>(utf8Json.ToArray())) ?? throw new System.Text.Json.JsonException(\"Volt: null state\");");
        }
        else
        {
            sb.AppendLine("            using var document = System.Text.Json.JsonDocument.Parse(new ReadOnlySequence<byte>(utf8Json.ToArray()));");
            sb.AppendLine("            var root = document.RootElement;");
            foreach (var prop in island.Properties)
            {
                var method = ReadMethod(prop);
                if (prop.IsNullable || prop.Kind == PropKind.String)
                    sb.AppendLine($"            var p_{prop.CodeName} = root.TryGetProperty({SymbolDisplay.FormatLiteral(prop.JsonName, quote: true)}, out var p_{prop.CodeName}El) && p_{prop.CodeName}El.ValueKind != System.Text.Json.JsonValueKind.Null ? p_{prop.CodeName}El.{method} : null;");
                else
                    sb.AppendLine($"            var p_{prop.CodeName} = root.GetProperty({SymbolDisplay.FormatLiteral(prop.JsonName, quote: true)}).{method};");
            }
            if (island.UseCtorInit)
            {
                var args = string.Join(", ", island.Properties.Select(p => "p_" + p.CodeName + (p.IsNullable && p.Kind != PropKind.String ? "!.Value" : "")));
                sb.AppendLine($"            return new {stateFqn}({args});");
            }
            else
            {
                var inits = string.Join(", ", island.Properties.Select(p =>
                    $"{{ {p.CodeName} = p_{p.CodeName}" + (p.IsNullable && p.Kind != PropKind.String ? " ?? default" : "") + " }}"));
                sb.AppendLine($"            return new {stateFqn} {string.Join(" ", inits)};");
            }
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string NestedClassName(string ns, string typeFqn)
    {
        // strip "Namespace." prefix → possibly nested "Outer.Inner"
        var rel = typeFqn;
        if (ns.Length > 0 && rel.StartsWith(ns + ".", StringComparison.Ordinal))
            rel = rel.Substring(ns.Length + 1);
        return rel.Replace(".", ".");
    }

    private static string WriteMethod(PropModel prop) => prop.Kind switch
    {
        PropKind.Int32 or PropKind.Int64 or PropKind.Double or PropKind.Single or PropKind.Decimal => "Number",
        PropKind.Boolean => "Boolean",
        PropKind.String => "String",
        _ => "String",
    };

    private static string ReadMethod(PropModel prop) => prop.Kind switch
    {
        PropKind.Int32 => "GetInt32()",
        PropKind.Int64 => "GetInt64()",
        PropKind.Boolean => "GetBoolean()",
        PropKind.Double => "GetDouble()",
        PropKind.Single => "GetSingle()",
        PropKind.Decimal => "GetDecimal()",
        _ => "GetString()",
    };
}
