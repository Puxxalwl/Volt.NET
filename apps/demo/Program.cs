using Volt;

// Demo app: `volt export` (VOLT_RUN_MODE=export) produces the static site deployed to GitHub Pages.
return Environment.GetEnvironmentVariable("VOLT_TRANSPORT") == "kestrel"
    ? VoltApp.Run(args)
    : VoltServerApp.Run(args);
