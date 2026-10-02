// Volt writes only initialized spans — skip stackalloc zero-init on the hot path.
[module: System.Runtime.CompilerServices.SkipLocalsInit]
