// 
// TypeSystemParser.cs
//  
// Author:
//       Mike Krüger <mkrueger@novell.com>
// 
// Copyright (c) 2011 Mike Krüger <mkrueger@novell.com>
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using MonoDevelop.Ide.TypeSystem;
using System.Collections.Generic;
using MonoDevelop.Projects;
using MonoDevelop.CSharp.Project;
using MonoDevelop.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis;
using System.Threading.Tasks;
using MonoDevelop.Ide;

namespace MonoDevelop.CSharp.Parser
{
	sealed class TypeSystemParser : MonoDevelop.Ide.TypeSystem.TypeSystemParser
	{
		public override System.Threading.Tasks.Task<ParsedDocument> Parse (MonoDevelop.Ide.TypeSystem.ParseOptions options, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
		{
			var fileName = options.FileName;
			var project = options.Project;
			var result = new CSharpParsedDocument (options, fileName);

			if (project != null) {
				
				var projectFile = project.Files.GetFile (fileName);
				if (projectFile != null && !project.IsCompileable (projectFile.FilePath))
					result.Flags |= ParsedDocumentFlags.NonSerializable;
			}

			if (project != null) {
				var curDoc = options.RoslynDocument;
				if (curDoc == null) {
					var curProject = IdeApp.TypeSystemService.GetCodeAnalysisProject (project);
					if (curProject != null) {
						var documentId = IdeApp.TypeSystemService.GetDocumentId (project, fileName);
						result.DocumentId = documentId;
					}
				}
			} else {
				var compilerArguments = GetCompilerArguments (project);
				result.ParsedUnit = CSharpSyntaxTree.ParseText (SourceText.From (options.Content.Text), compilerArguments, fileName);
			}

			DateTime time;
			try {
				time = System.IO.File.GetLastWriteTimeUtc (fileName);
			} catch (Exception) {
				time = DateTime.UtcNow;
			}
			result.LastWriteTimeUtc = time;
			return Task.FromResult<ParsedDocument> (result);
		}

		public static CSharpParseOptions GetCompilerArguments (MonoDevelop.Projects.Project project)
		{
			var compilerArguments = new CSharpParseOptions ();
	//		compilerArguments.TabSize = 1;

			if (project == null || MonoDevelop.Ide.IdeApp.Workspace == null) {
				// compilerArguments.AllowUnsafeBlocks = true;
				return compilerArguments;
			}

			var configuration = project.GetConfiguration (MonoDevelop.Ide.IdeApp.Workspace.ActiveConfiguration) as DotNetProjectConfiguration;
			if (configuration == null)
				return compilerArguments;

			compilerArguments = compilerArguments.WithPreprocessorSymbols (configuration.GetDefineSymbols ());

			var par = configuration.CompilationParameters as CSharpCompilerParameters;
			if (par == null)
				return compilerArguments;

			 
			// compilerArguments.AllowUnsafeBlocks = par.UnsafeCode;
			compilerArguments = compilerArguments.WithLanguageVersion (par.LangVersion);
//			compilerArguments.CheckForOverflow = par.GenerateOverflowChecks;

//			compilerArguments.WarningLevel = par.WarningLevel;
//			compilerArguments.TreatWarningsAsErrors = par.TreatWarningsAsErrors;
//			if (!string.IsNullOrEmpty (par.NoWarnings)) {
//				foreach (var warning in par.NoWarnings.Split (';', ',', ' ', '\t')) {
//					int w;
//					try {
//						w = int.Parse (warning);
//					} catch (Exception) {
//						continue;
//					}
//					compilerArguments.DisabledWarnings.Add (w);
//				}
//			}
			
			return compilerArguments;
		}
	}
}

