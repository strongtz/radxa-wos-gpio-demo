# i2cdetect-wos

A Windows .NET implementation of the Linux `i2cdetect` tool, using `Windows.Devices.I2c`.

## Build

```pwsh
dotnet build
```

## Usage

Run from the output directory:

```pwsh
# List all I2C buses
i2cdetect-wos.exe -l

# Scan bus 1 (interactive mode)
i2cdetect-wos.exe 1

# Scan bus 1 (no confirmation)
i2cdetect-wos.exe -y 1

# Scan all addresses (0x00 - 0x7F)
i2cdetect-wos.exe -a 1
```

## Bus Identification

The tool lists buses with an assigned index (e.g., 1, 2, 3) and their friendly name/Device ID. You can use this index as the `I2CBUS` argument.

## Limitations

- **Probing Mode**: Only Read Byte probing is supported (`-r`). Quick Write probing (`-q`) is unreliable on many Windows I2C drivers (failure to support 0-byte writes) and has been disabled. The tool defaults to Read Byte probing for all addresses.
- **In-Use Addresses**: Addresses used by other drivers/apps (exclusive mode) will show as `UU`.
