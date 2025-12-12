using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.I2c;

namespace i2cdetect_wos
{
    internal class Program
    {
        static bool opt_yes = false;
        static bool opt_all = false;
        static bool opt_list = false;

        static async Task<int> Main(string[] args)
        {
            var parsedArgs = ParseArguments(args);
            if (parsedArgs == null) return 1;

            if (opt_list)
            {
                await PrintI2cBusses();
                return 0;
            }

            if (parsedArgs.Count < 1)
            {
                Console.Error.WriteLine("Error: No i2c-bus specified!");
                PrintHelp();
                return 1;
            }

            string busArg = parsedArgs[0];
            string? busId = await LookupI2cBus(busArg);
            if (busId == null)
            {
                Console.Error.WriteLine($"Error: I2C bus '{busArg}' not found.");
                return 1;
            }

            int first = 0x03;
            int last = 0x77;
            if (opt_all)
            {
                first = 0x00;
                last = 0x7F;
            }

            // Parse optional FIRST LAST
            if (parsedArgs.Count > 1)
            {
                if (!TryParseInt(parsedArgs[1], out int f))
                {
                    Console.Error.WriteLine("Error: FIRST argument not a number!");
                    PrintHelp();
                    return 1;
                }
                first = f;
            }
            if (parsedArgs.Count > 2)
            {
                if (!TryParseInt(parsedArgs[2], out int l))
                {
                    Console.Error.WriteLine("Error: LAST argument not a number!");
                    PrintHelp();
                    return 1;
                }
                last = l;
            }

            // Confirmation
            if (!opt_yes)
            {
                Console.Error.WriteLine("WARNING! This program can confuse your I2C bus, cause data loss and worse!");
                Console.Error.WriteLine($"I will probe I2C bus {busArg}");
                Console.Error.WriteLine($"I will probe address range 0x{first:x2}-0x{last:x2}.");
                Console.Error.Write("Continue? [Y/n] ");
                string? input = Console.ReadLine();
                if (string.IsNullOrEmpty(input) || (input.ToUpper() != "Y" && input.ToUpper() != "YES"))
                {
                    Console.Error.WriteLine("Aborting on user request.");
                    return 0;
                }
            }

            await ScanI2cBus(busId, first, last);

            return 0;
        }

        static List<string>? ParseArguments(string[] args)
        {
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("-"))
                {
                    // Handle flags
                    foreach (char c in arg.Substring(1))
                    {
                        switch (c)
                        {
                            case 'y': opt_yes = true; break;
                            case 'a': opt_all = true; break;
                            case 'l': opt_list = true; break;
                            case 'q':
                                Console.Error.WriteLine("Warning: -q (Quick Write) is not supported reliably on Windows. Using Read probe instead.");
                                break;
                            case 'r':
                                // Default behavior now
                                break;
                            case 'h':
                            case '?':
                                PrintHelp();
                                return null;
                            default:
                                Console.Error.WriteLine($"Error: Unknown option -{c}");
                                PrintHelp();
                                return null;
                        }
                    }
                }
                else
                {
                    positional.Add(arg);
                }
            }
            return positional;
        }

        static void PrintHelp()
        {
            Console.Error.WriteLine("Usage: i2cdetect [-y] [-a] [-r] I2CBUS [FIRST LAST]");
            Console.Error.WriteLine("       i2cdetect -l");
            Console.Error.WriteLine("  I2CBUS is an integer or an I2C bus name");
            Console.Error.WriteLine("  If provided, FIRST and LAST limit the probing range.");
            Console.Error.WriteLine("  Note: This Windows version always uses 'SMBus Receive Byte' (read) for probing.");
        }

        static bool TryParseInt(string s, out int value)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(s.Substring(2), NumberStyles.HexNumber, null, out value);
            }
            return int.TryParse(s, out value);
        }

        static async Task PrintI2cBusses()
        {
            string aqs = I2cDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(aqs);

            var sortedDevices = devices.OrderBy(d => d.Id).ToList();
            
            int index = 1;
            Console.WriteLine("i2c-no\tName\t\t\tDeviceId");
            foreach (var d in sortedDevices)
            {
                object? friendlyNameObj = d.Properties.TryGetValue("System.DeviceInterface.Spb.ControllerFriendlyName", out var val) ? val : d.Name;
                string friendlyName = friendlyNameObj?.ToString() ?? d.Name;
                
                string shortId = d.Id.Length > 40 ? "..." + d.Id.Substring(d.Id.Length - 40) : d.Id;

                Console.WriteLine($"{index}\t{friendlyName,-20}\t{shortId}");
                index++;
            }
        }

        static async Task<string?> LookupI2cBus(string arg)
        {
            string aqs = I2cDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(aqs);
            var sortedDevices = devices.OrderBy(d => d.Id).ToList();

            if (int.TryParse(arg, out int index))
            {
                if (index >= 1 && index <= sortedDevices.Count)
                {
                    return sortedDevices[index - 1].Id;
                }
            }

            foreach (var d in sortedDevices)
            {
                if (d.Id.Contains(arg, StringComparison.OrdinalIgnoreCase) || d.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))
                {
                    return d.Id;
                }
            }

            return null;
        }

        static async Task ScanI2cBus(string busId, int first, int last)
        {
            Console.WriteLine("     0  1  2  3  4  5  6  7  8  9  a  b  c  d  e  f");

            for (int i = 0; i < 128; i += 16)
            {
                Console.Write($"{i:x2}: ");
                for (int j = 0; j < 16; j++)
                {
                    int addr = i + j;

                    // Skip unwanted addresses
                    if (addr < first || addr > last)
                    {
                        Console.Write("   ");
                        continue;
                    }

                    // Probe using Read logic
                    var result = await Probe(busId, addr);
                    Console.Write(result);
                }
                Console.WriteLine();
            }
        }

        static async Task<string> Probe(string busId, int addr)
        {
            I2cDevice? device = null;
            try
            {
                var settings = new I2cConnectionSettings(addr);
                settings.BusSpeed = I2cBusSpeed.StandardMode; // Default to 100kHz
                settings.SharingMode = I2cSharingMode.Shared; 

                try 
                {
                    device = await I2cDevice.FromIdAsync(busId, settings);
                }
                catch 
                {
                     return "UU ";
                }

                if (device == null)
                {
                    return "UU ";
                }

                // SMBus Receive Byte: Read 1 byte.
                // This is the only reliable probe method on Windows since Quick Write (0 bytes) isn't supported by many drivers.
                byte[] buffer = new byte[1];
                I2cTransferResult result = device.ReadPartial(buffer);

                if (result.Status == I2cTransferStatus.FullTransfer)
                {
                    return $"{addr:x2} ";
                }
                else if (result.Status == I2cTransferStatus.SlaveAddressNotAcknowledged)
                {
                    return "-- ";
                }
                else
                {
                    return "-- ";
                }
            }
            catch (Exception)
            {
                return "-- ";
            }
            finally
            {
                device?.Dispose();
            }
        }
    }
}
