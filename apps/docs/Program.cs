using Volt;

// M2: default to the built-in zero-allocation server; VOLT_TRANSPORT=kestrel for the Kestrel bridge
return Environment.GetEnvironmentVariable("VOLT_TRANSPORT") == "kestrel"
    ? VoltApp.Run(args)
    : VoltServerApp.Run(args);
