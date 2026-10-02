namespace Volt;

/// <summary>
/// M6: request middleware — the classic onion. Runs around the async pipeline
/// (page renders, island actions, the no-JS fallback, internal endpoints) in
/// registration order. <paramref name="next"/> continues the chain.
/// </summary>
/// <remarks>
/// While any middleware is registered, <see cref="VoltEngine.TryServeFast"/> is
/// bypassed: cached SSG pages must not skip middleware (auth, redirects, logging).
/// The zero-allocation fast path stays active only for middleware-free apps.
/// </remarks>
public delegate Task VoltMiddleware(VoltHttpContext ctx, Func<Task> next);
