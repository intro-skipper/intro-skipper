# How to enable plugin debug logs

1. Browse to your Jellyfin config folder
2. Make a backup copy of `config/logging.default.json` before editing it
3. Open `config/logging.default.json` with a text editor. The top lines should look something like this:

```jsonc
{
    "Serilog": {
        "MinimumLevel": {
            "Default": "Information",
            "Override": {
                "Microsoft": "Warning",
                "System": "Warning"
            }
        },
        // rest of file ommited for brevity
    }
}
```

4. Inside the `Override` section, add a new entry for `ConfusedPolarBear` and set it to `Debug`. The modified file should now look like this:

```jsonc
{
    "Serilog": {
        "MinimumLevel": {
            "Default": "Information",
            "Override": {
                "Microsoft": "Warning",
                "System": "Warning",                // be sure to add the trailing comma after "Warning",
                "IntroSkipper": "Debug"             // newly added line
            }
        },
        // rest of file ommited for brevity
    }
}
```

5. Save the file and restart Jellyfin

## How to enable verbose logs

To enable verbose log messages, set the log level to `Verbose` instead of `Debug` in step 4.
