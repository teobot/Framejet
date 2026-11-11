using System;
using System.Globalization;

namespace Framejet.Core
{
    /// <summary>
    /// The app's arguments: <c>--selftest &lt;report&gt;</c>, <c>--seconds &lt;n&gt;</c> and <c>--settings &lt;path&gt;</c>.
    /// Unknown arguments are ignored.
    /// </summary>
    public sealed class CommandLine
    {
        /// <summary>Where the self-test writes its report; null for a normal launch.</summary>
        public string SelfTestReport { get; private set; }

        /// <summary>How long each self-test phase measures.</summary>
        public int Seconds { get; private set; } = 4;

        /// <summary>The settings file (the log goes next to it); null for the default location.</summary>
        public string SettingsPath { get; private set; }

        /// <summary>Throws <see cref="ArgumentException"/> when an option is missing its value or the seconds aren't a positive number.</summary>
        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine();
            if (args == null) return result;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--selftest":
                        result.SelfTestReport = ValueOf(args, ref i);
                        break;
                    case "--settings":
                        result.SettingsPath = ValueOf(args, ref i);
                        break;
                    case "--seconds":
                        var text = ValueOf(args, ref i);
                        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
                            throw new ArgumentException("--seconds needs a positive whole number, not \"" + text + "\"");
                        result.Seconds = seconds;
                        break;
                }
            }
            return result;
        }

        /// <summary>The value after the option at <paramref name="i"/>, which then points at the value.</summary>
        static string ValueOf(string[] args, ref int i)
        {
            string option = args[i];
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(option + " needs a value");
            i++;
            return args[i];
        }
    }
}
