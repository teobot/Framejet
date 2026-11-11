using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Framejet.Core;

namespace Framejet
{
    static class Program
    {
        // Dependencies are embedded as deps.<name>.dll resources. Nothing here may mention a type
        // from an embedded assembly: the hook must exist before any of them is first needed.
        static readonly object ResolveLock = new object();
        static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        static Program()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
        }

        static Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                lock (ResolveLock)
                {
                    Assembly cached;
                    if (Loaded.TryGetValue(name, out cached)) return cached;
                    using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("deps." + name + ".dll"))
                    {
                        if (s == null) return null;
                        var bytes = new byte[s.Length];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = s.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) return null;
                            read += n;
                        }
                        Assembly asm = Assembly.Load(bytes);
                        Loaded[name] = asm;
                        return asm;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        [STAThread]
        static int Main(string[] args) => Run(args);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run(string[] args)
        {
            CommandLine cmd;
            try
            {
                cmd = CommandLine.Parse(args);
            }
            catch (ArgumentException ex)
            {
                // A self-test runs unattended, so it never waits on a message box.
                if (Array.IndexOf(args, "--selftest") < 0) MessageBox.Show(ex.Message, "Framejet", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            using (var instance = SingleInstance.TryAcquire())
            {
                if (instance == null)
                {
                    if (cmd.SelfTestReport != null)
                    {
                        SelfTestRunner.WriteFailure(cmd.SelfTestReport, "Framejet is already running");
                        return 1;
                    }
                    SingleInstance.ActivateExisting();
                    return 0;
                }

                UnhandledExceptions.Install(cmd); // before any window exists
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(cmd));
                return Environment.ExitCode;
            }
        }
    }
}
