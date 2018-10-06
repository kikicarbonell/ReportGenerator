using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by dotCover.
    /// </summary>
    internal class DotCoverParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(DotCoverParser));

        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private static Regex lambdaMethodNameRegex = new Regex(@"<.+>.+__.+\(.*\)", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze if a method name is generated by compiler.
        /// </summary>
        private static Regex compilerGeneratedMethodNameRegex = new Regex(@"<(?<CompilerGeneratedName>.+)>.+__.+MoveNext\(\):.+$", RegexOptions.Compiled);

        /// <summary>
        /// Initializes a new instance of the <see cref="DotCoverParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal DotCoverParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report</param>
        /// <returns>The parser result.</returns>
        public override ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var modules = report.Descendants("Assembly")
                .ToArray();
            var files = report.Descendants("File").ToArray();

            var assemblyNames = modules
                .Select(m => m.Attribute("Name").Value)
                .Distinct()
                .Where(a => this.AssemblyFilter.IsElementIncludedInReport(a))
                .OrderBy(a => a)
                .ToArray();

            foreach (var assemblyName in assemblyNames)
            {
                assemblies.Add(this.ProcessAssembly(modules, files, assemblyName));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), false, this.ToString());
            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="files">The files.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(XElement[] modules, XElement[] files, string assemblyName)
        {
            Logger.DebugFormat("  " + Resources.CurrentAssembly, assemblyName);

            var assemblyElement = modules
                .Where(m => m.Attribute("Name").Value.Equals(assemblyName));

            var classNames = assemblyElement
                .Elements("Namespace")
                .Elements("Type")
                .Concat(assemblyElement.Elements("Type"))
                .Where(c => !Regex.IsMatch(c.Attribute("Name").Value, "<.*>.+__", RegexOptions.Compiled))
                .Select(c => c.Parent.Attribute("Name").Value + "." + c.Attribute("Name").Value)
                .Distinct()
                .Where(c => this.ClassFilter.IsElementIncludedInReport(c))
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => this.ProcessClass(modules, files, assembly, className));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="files">The files.</param>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        private void ProcessClass(XElement[] modules, XElement[] files, Assembly assembly, string className)
        {
            var assemblyElement = modules
                .Where(m => m.Attribute("Name").Value.Equals(assembly.Name));

            var fileIdsOfClass = assemblyElement
                .Elements("Namespace")
                .Elements("Type")
                .Concat(assemblyElement.Elements("Type"))
                .Where(c => (c.Parent.Attribute("Name").Value + "." + c.Attribute("Name").Value).Equals(className))
                .Descendants("Statement")
                .Select(c => c.Attribute("FileIndex").Value)
                .Distinct()
                .ToArray();

            var filteredFilesOfClass = fileIdsOfClass
                .Select(fileId =>
                    new
                    {
                        FileId = fileId,
                        FilePath = files.First(f => f.Attribute("Index").Value == fileId).Attribute("Name").Value
                    })
                .Where(f => this.FileFilter.IsElementIncludedInReport(f.FilePath))
                .ToArray();

            // If all files are removed by filters, then the whole class is omitted
            if (fileIdsOfClass.Length == 0 || filteredFilesOfClass.Length > 0)
            {
                var @class = new Class(className, assembly);

                foreach (var file in filteredFilesOfClass)
                {
                    @class.AddFile(ProcessFile(modules, file.FileId, @class, file.FilePath));
                }

                assembly.AddClass(@class);
            }
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="fileId">The file id.</param>
        /// <param name="class">The class.</param>
        /// <param name="filePath">The file path.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private static CodeFile ProcessFile(XElement[] modules, string fileId, Class @class, string filePath)
        {
            var assemblyElement = modules
                .Where(m => m.Attribute("Name").Value.Equals(@class.Assembly.Name));

            var methodsOfFile = assemblyElement
               .Elements("Namespace")
               .Elements("Type")
               .Concat(assemblyElement.Elements("Type"))
               .Where(c => (c.Parent.Attribute("Name").Value + "." + c.Attribute("Name").Value).Equals(@class.Name))
               .Descendants("Method")
               .ToArray();

            var statements = methodsOfFile
               .Elements("Statement")
               .Where(c => c.Attribute("FileIndex").Value == fileId)
               .Select(c => new
               {
                   LineNumberStart = int.Parse(c.Attribute("Line").Value, CultureInfo.InvariantCulture),
                   LineNumberEnd = int.Parse(c.Attribute("EndLine").Value, CultureInfo.InvariantCulture),
                   Visited = c.Attribute("Covered").Value == "True"
               })
               .OrderBy(seqpnt => seqpnt.LineNumberEnd)
               .ToArray();

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (statements.Length > 0)
            {
                coverage = new int[statements[statements.LongLength - 1].LineNumberEnd + 1];
                lineVisitStatus = new LineVisitStatus[statements[statements.LongLength - 1].LineNumberEnd + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var statement in statements)
                {
                    for (int lineNumber = statement.LineNumberStart; lineNumber <= statement.LineNumberEnd; lineNumber++)
                    {
                        int visits = statement.Visited ? 1 : 0;
                        coverage[lineNumber] = coverage[lineNumber] == -1 ? visits : Math.Min(coverage[lineNumber] + visits, 1);
                        lineVisitStatus[lineNumber] = lineVisitStatus[lineNumber] == LineVisitStatus.Covered || statement.Visited ? LineVisitStatus.Covered : LineVisitStatus.NotCovered;
                    }
                }
            }

            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus);

            SetCodeElements(codeFile, fileId, methodsOfFile);

            return codeFile;
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="fileId">The id of the file.</param>
        /// <param name="methods">The methods.</param>
        private static void SetCodeElements(CodeFile codeFile, string fileId, IEnumerable<XElement> methods)
        {
            foreach (var method in methods)
            {
                string methodName = ExtractMethodName(method.Parent.Attribute("Name").Value, method.Attribute("Name").Value);

                if (lambdaMethodNameRegex.IsMatch(methodName))
                {
                    continue;
                }

                CodeElementType type = CodeElementType.Method;

                if (methodName.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                    || methodName.StartsWith("set_", StringComparison.OrdinalIgnoreCase))
                {
                    type = CodeElementType.Property;
                    methodName = methodName.Substring(4);
                }

                var seqpnts = method
                    .Elements("Statement")
                    .Where(c => c.Attribute("FileIndex").Value == fileId)
                    .Select(c => new
                    {
                        LineNumberStart = int.Parse(c.Attribute("Line").Value, CultureInfo.InvariantCulture),
                        LineNumberEnd = int.Parse(c.Attribute("EndLine").Value, CultureInfo.InvariantCulture)
                    })
                    .ToArray();

                if (seqpnts.Length > 0)
                {
                    codeFile.AddCodeElement(new CodeElement(methodName, type, seqpnts.Min(s => s.LineNumberStart), seqpnts.Max(s => s.LineNumberEnd)));
                }
            }
        }

        /// <summary>
        /// Extracts the method name. For async methods the original name is returned.
        /// </summary>
        /// <param name="typeName">The name of the class.</param>
        /// <param name="methodName">The full method name.</param>
        /// <returns>The method name.</returns>
        private static string ExtractMethodName(string typeName, string methodName)
        {
            // Quick check before expensive regex is called
            if (methodName.Contains("MoveNext()"))
            {
                Match match = compilerGeneratedMethodNameRegex.Match(typeName + methodName);

                if (match.Success)
                {
                    return match.Groups["CompilerGeneratedName"].Value + "()";
                }
            }

            return methodName.Substring(0, methodName.LastIndexOf(':'));
        }
    }
}
