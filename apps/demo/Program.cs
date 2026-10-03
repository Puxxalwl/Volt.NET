using Volt;

// Demo app: `volt export` (VOLT_RUN_MODE=export) produces the static site deployed to GitHub Pages.
// VOLT_BASE_URL overrides the canonical URL in the sitemap/robots/SEO tags (used by the Pages deploy).
var options = new VoltOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("VOLT_BASE_URL") ?? "http://localhost:5000",
};
return Environment.GetEnvironmentVariable("VOLT_TRANSPORT") == "kestrel"
    ? VoltApp.Run(args, options)
    : VoltServerApp.Run(args, options);
