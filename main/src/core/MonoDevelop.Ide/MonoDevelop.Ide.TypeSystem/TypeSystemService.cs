// 
// IdeApp.TypeSystemService.cs
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
using System.Collections.Generic;
using System.Linq;
using System.IO;
using MonoDevelop.Projects;
using Mono.Addins;
using MonoDevelop.Core;
using System.Threading;
using System.Xml;
using ICSharpCode.NRefactory.Utils;
using System.Threading.Tasks;
using MonoDevelop.Ide.Extensions;
using MonoDevelop.Core.Assemblies;
using MonoDevelop.Ide.Editor;
using MonoDevelop.Core.Text;
using MonoDevelop.Ide.RoslynServices.Options;
using MonoDevelop.Ide.Gui.Documents;
using MonoDevelop.Ide.Composition;
using Microsoft.CodeAnalysis.Text;

namespace MonoDevelop.Ide.TypeSystem
{
	[DefaultServiceImplementation]
	public partial class TypeSystemService: Service
	{
		const string CurrentVersion = "1.1.9";

		DocumentManager documentManager;
		DesktopService desktopService;
		RootWorkspace rootWorkspace;
		CompositionManager compositionManager;

		[Obsolete]
		IEnumerable<TypeSystemParserNode> parsers;

		public Microsoft.CodeAnalysis.SyntaxAnnotation InsertionModeAnnotation { get; } = new Microsoft.CodeAnalysis.SyntaxAnnotation();

		// Preferences
		internal static RoslynPreferences Preferences { get; } = new RoslynPreferences ();
		internal static ConfigurationProperty<bool> EnableSourceAnalysis = ConfigurationProperty.Create ("MonoDevelop.AnalysisCore.AnalysisEnabled_V2", true);

		internal MonoDevelopRuleSetManager RuleSetManager { get; } = new MonoDevelopRuleSetManager ();

		static MiscellaneousFilesWorkspace miscellaneousFilesWorkspace;

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal IEnumerable<TypeSystemParserNode> Parsers {
			get {
				return parsers;
			}
			set {
				parsers = value;
			}
		}

		protected override async Task OnInitialize (ServiceProvider serviceProvider)
		{
			IntitializeTrackedProjectHandling ();

			serviceProvider.WhenServiceInitialized<CompositionManager> (s => {
				miscellaneousFilesWorkspace = CompositionManager.Instance.GetExportedValue<MiscellaneousFilesWorkspace> ();
				serviceProvider.WhenServiceInitialized<DocumentManager> (dm => {
					documentManager = dm;
				});
			});
			serviceProvider.WhenServiceInitialized<RootWorkspace> (s => {
				rootWorkspace = s;
				rootWorkspace.ActiveConfigurationChanged += HandleActiveConfigurationChanged;
			});

			RoslynServices.RoslynService.Initialize ();
			CleanupCache ();

			#pragma warning disable CS0618, 612 // Type or member is obsolete
			parsers = AddinManager.GetExtensionNodes<TypeSystemParserNode> ("/MonoDevelop/TypeSystem/Parser");
			bool initialLoad = true;
			AddinManager.AddExtensionNodeHandler ("/MonoDevelop/TypeSystem/Parser", delegate (object sender, ExtensionNodeEventArgs args) {
				//refresh entire list to respect insertbefore/insertafter ordering
				if (!initialLoad)
					parsers = AddinManager.GetExtensionNodes<TypeSystemParserNode> ("/MonoDevelop/TypeSystem/Parser");
			});
			#pragma warning restore CS0618, 612 // Type or member is obsolete
			initialLoad = false;

			try {
				compositionManager = await serviceProvider.GetService<CompositionManager> ().ConfigureAwait (false);
				emptyWorkspace = new MonoDevelopWorkspace (compositionManager.HostServices, null, this);
				await emptyWorkspace.Initialize ().ConfigureAwait (false);
			} catch (Exception e) {
				LoggingService.LogFatalError ("Can't create roslyn workspace", e); 
			}

			FileService.FileChanged += FileService_FileChanged;

			desktopService = await serviceProvider.GetService<DesktopService> ();

			await serviceProvider.GetService<HelpService> ();
		}

		protected override Task OnDispose ()
		{
			FileService.FileChanged -= FileService_FileChanged;
			if (rootWorkspace != null)
				rootWorkspace.ActiveConfigurationChanged -= HandleActiveConfigurationChanged;
			FinalizeTrackedProjectHandling ();
			return Task.CompletedTask;
		}

		void FileService_FileChanged (object sender, FileEventArgs e)
		{
			List<string> filesToUpdate = null;
			foreach (var file in e) {
				// Open documents are handled by the Document class itself.
				if (documentManager?.GetDocument (file.FileName) != null)
					continue;

				foreach (var w in workspaces) {
					var documentIds = w.CurrentSolution.GetDocumentIdsWithFilePath (file.FileName);
					if (documentIds.IsEmpty)
						continue;

					if (filesToUpdate == null)
						filesToUpdate = new List<string> ();
					filesToUpdate.Add (file.FileName);
					goto found;
				}
			found:;

			}
			if (filesToUpdate == null || filesToUpdate.Count == 0)
				return;

			Task.Run (async delegate {
				try {
					foreach (var file in filesToUpdate) {
						var text = MonoDevelop.Core.Text.StringTextSource.ReadFrom (file).Text;
						foreach (var w in workspaces)
							await w.UpdateFileContent (file, text);
					}

					Gtk.Application.Invoke ((o, args) => {
						if (documentManager != null) {
							foreach (var w in documentManager.Documents)
								w.DocumentContext?.ReparseDocument ();
						}
					});
				} catch (Exception) { }
			});
		}

		internal async Task<MonoDevelopWorkspace> CreateEmptyWorkspace ()
		{
			var ws = new MonoDevelopWorkspace (compositionManager.HostServices, null, this);
			await ws.Initialize ();
			return ws;
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		public TypeSystemParser GetParser (string mimeType, string buildAction = BuildAction.Compile)
		{
			var n = GetTypeSystemParserNode (mimeType, buildAction);
			return n != null ? n.Parser : null;
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal TypeSystemParserNode GetTypeSystemParserNode (string mimeType, string buildAction)
		{
			foreach (var mt in desktopService.GetMimeTypeInheritanceChain (mimeType)) {
				var provider = Parsers.FirstOrDefault (p => p.CanParse (mt, buildAction));
				if (provider != null)
					return provider;
			}
			return null;
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		public Task<ParsedDocument> ParseFile (Project project, string fileName, CancellationToken cancellationToken = default(CancellationToken))
		{
			StringTextSource text;

			try {
				if (!File.Exists (fileName))
					return TaskUtil.Default<ParsedDocument>();
				text = StringTextSource.ReadFrom (fileName);
			} catch (Exception) {
				return TaskUtil.Default<ParsedDocument>();
			}

			return ParseFile (project, fileName, desktopService.GetMimeTypeForUri (fileName), text, cancellationToken);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		public Task<ParsedDocument> ParseFile (ParseOptions options, string mimeType, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (options == null)
				throw new ArgumentNullException (nameof(options));
			if (options.FileName == null)
				throw new ArgumentNullException ("options.FileName");

			var parser = GetParser (mimeType);
			if (parser == null)
				return TaskUtil.Default<ParsedDocument>();

			var t = Counters.ParserService.FileParsed.BeginTiming (options.FileName);
			try {
				var result = parser.Parse (options, cancellationToken);
				return result ?? TaskUtil.Default<ParsedDocument>();
			} catch (OperationCanceledException) {
				return TaskUtil.Default<ParsedDocument>();
			} catch (Exception e) {
				LoggingService.LogError ("Exception while parsing: " + e);
				return TaskUtil.Default<ParsedDocument>();
			} finally {
				t.Dispose ();
			}
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal bool CanParseProjections (Project project, string mimeType, string fileName)
		{
			var projectFile = project.GetProjectFile (fileName);
			if (projectFile == null)
				return false;
			var parser = GetParser (mimeType, projectFile.BuildAction);
			if (parser == null)
				return false;
			return parser.CanGenerateProjection (mimeType, projectFile.BuildAction, project.SupportedLanguages);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		public Task<ParsedDocument> ParseFile (Project project, string fileName, string mimeType, ITextSource content, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ParseFile (new ParseOptions { FileName = fileName, Project = project, Content = content }, mimeType, cancellationToken);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		public Task<ParsedDocument> ParseFile (Project project, string fileName, string mimeType, TextReader content, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ParseFile (project, fileName, mimeType, new StringTextSource (content.ReadToEnd ()), cancellationToken);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		public Task<ParsedDocument> ParseFile (Project project, IReadonlyTextDocument data, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ParseFile (project, data.FileName, data.MimeType, data, cancellationToken);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal async Task<ParsedDocumentProjection> ParseProjection (ParseOptions options, string mimeType, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (options == null)
				throw new ArgumentNullException (nameof(options));
			if (options.FileName == null)
				throw new ArgumentNullException ("fileName");

			var parser = GetParser (mimeType, options.BuildAction);
			if (parser == null || !parser.CanGenerateProjection (mimeType, options.BuildAction, options.Project?.SupportedLanguages))
				return null;

			var t = Counters.ParserService.FileParsed.BeginTiming (options.FileName);
			try {
				var result = await parser.GenerateParsedDocumentProjection (options, cancellationToken);
				if (cancellationToken.IsCancellationRequested)
					return null;

				if (options.Project != null) {
					var ws = workspaces.First () ;
					var projectId = ws.GetProjectId (options.Project);

					if (projectId != null) {
						var projectFile = options.Project.GetProjectFile (options.FileName);
						if (projectFile != null) {
							ws.UpdateProjectionEntry (projectFile, result.Projections);
							await ws.LoadLock.WaitAsync ();
							try {
								foreach (var projection in result.Projections) {
									var docId = ws.GetDocumentId (projectId, projection.Document.FileName);
									if (docId != null) {
										ws.InformDocumentTextChange (docId, new MonoDevelopSourceText (projection.Document));
									}
								}
							} finally {
								ws.LoadLock.Release ();
							}
						}
					}
				}
				return result;
			} catch (AggregateException ae) {
				ae.Flatten ().Handle (x => x is OperationCanceledException);
				return null;
			} catch (OperationCanceledException) {
				return null;
			} catch (Exception e) {
				LoggingService.LogError ("Exception while parsing: " + e);
				return null;
			} finally {
				t.Dispose ();
			}
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal Task<ParsedDocumentProjection> ParseProjection (Project project, string fileName, string mimeType, ITextSource content, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ParseProjection (new ParseOptions { FileName = fileName, Project = project, Content = content }, mimeType, cancellationToken);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal Task<ParsedDocumentProjection> ParseProjection (Project project, string fileName, string mimeType, TextReader content, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ParseProjection (project, fileName, mimeType, new StringTextSource (content.ReadToEnd ()), cancellationToken);
		}

		[Obsolete ("Use the Visual Studio Editor APIs")]
		internal Task<ParsedDocumentProjection> ParseProjection (Project project, IReadonlyTextDocument data, CancellationToken cancellationToken = default(CancellationToken))
		{
			return ParseProjection (project, data.FileName, data.MimeType, data, cancellationToken);
		}

	
		#region Folding parsers
		List<MimeTypeExtensionNode> foldingParsers;

		IEnumerable<MimeTypeExtensionNode> FoldingParsers {
			get {
				if (foldingParsers == null) {
					foldingParsers = new List<MimeTypeExtensionNode> ();
					AddinManager.AddExtensionNodeHandler ("/MonoDevelop/TypeSystem/FoldingParser", delegate (object sender, ExtensionNodeEventArgs args) {
						switch (args.Change) {
						case ExtensionChange.Add:
							foldingParsers.Add ((MimeTypeExtensionNode)args.ExtensionNode);
							break;
						case ExtensionChange.Remove:
							foldingParsers.Remove ((MimeTypeExtensionNode)args.ExtensionNode);
							break;
						}
					});
				}
				return foldingParsers;
			}
		}

		public IFoldingParser GetFoldingParser (string mimeType)
		{
			foreach (var mt in desktopService.GetMimeTypeInheritanceChain (mimeType)) {
				var node = FoldingParsers.FirstOrDefault (n => n.MimeType == mt);
				if (node != null)
					return node.CreateInstance () as IFoldingParser;
			}
			return null;
		}
		#endregion

		#region Parser Database Handling

		string GetCacheDirectory (TargetFramework framework)
		{
			var derivedDataPath = UserProfile.Current.CacheDir.Combine ("DerivedData");

			var name = StringBuilderCache.Allocate ();
			foreach (var ch in framework.Name) {
				if (char.IsLetterOrDigit (ch)) {
					name.Append (ch);
				} else {
					name.Append ('_');
				}
			}

			string result = derivedDataPath.Combine (StringBuilderCache.ReturnAndFree (name));
			try {
				if (!Directory.Exists (result))
					Directory.CreateDirectory (result);
			} catch (Exception e) {
				LoggingService.LogError ("Error while creating derived data directories.", e);
			}
			return result;
		}

		string InternalGetCacheDirectory (FilePath filename)
		{
			CanonicalizePath (ref filename);
			var assemblyCacheRoot = GetAssemblyCacheRoot (filename);
			try {
				if (!Directory.Exists (assemblyCacheRoot))
					return null;
				foreach (var dir in Directory.EnumerateDirectories (assemblyCacheRoot)) {
					string result;
					if (CheckCacheDirectoryIsCorrect (filename, dir, out result))
						return result;
				}
			} catch (Exception e) {
				LoggingService.LogError ("Error while getting derived data directories.", e);
			}
			return null;
		}

		/// <summary>
		/// Gets the cache directory for a projects derived data cache directory.
		/// If forceCreation is set to false the method may return null, if the cache doesn't exist.
		/// </summary>
		/// <returns>The cache directory.</returns>
		/// <param name="project">The project to get the cache for.</param>
		/// <param name="forceCreation">If set to <c>true</c> the creation is forced and the method doesn't return null.</param>
		public string GetCacheDirectory (Project project, bool forceCreation = false)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			return GetCacheDirectory (project.FileName, forceCreation);
		}

		readonly Dictionary<string, object> cacheLocker = new Dictionary<string, object> ();

		/// <summary>
		/// Gets the cache directory for arbitrary file names.
		/// If forceCreation is set to false the method may return null, if the cache doesn't exist.
		/// </summary>
		/// <returns>The cache directory.</returns>
		/// <param name="fileName">The file name to get the cache for.</param>
		/// <param name="forceCreation">If set to <c>true</c> the creation is forced and the method doesn't return null.</param>
		public string GetCacheDirectory (string fileName, bool forceCreation = false)
		{
			if (fileName == null)
				throw new ArgumentNullException (nameof(fileName));
			object locker;
			bool newLock;
			lock (cacheLocker) {
				if (!cacheLocker.TryGetValue (fileName, out locker)) {
					cacheLocker [fileName] = locker = new object ();
					newLock = true;
				} else {
					newLock = false;
				}
			}
			lock (locker) {
				var result = InternalGetCacheDirectory (fileName);
				if (newLock && result != null)
					TouchCache (result);
				if (forceCreation && result == null)
					result = CreateCacheDirectory (fileName);
				return result;
			}
		}

		struct CacheDirectoryInfo
		{
			public static readonly CacheDirectoryInfo Empty = new CacheDirectoryInfo ();

			public string FileName { get; set; }

			public string Version { get; set; }
		}

		readonly Dictionary<FilePath, CacheDirectoryInfo> cacheDirectoryCache = new Dictionary<FilePath, CacheDirectoryInfo> ();

		void CanonicalizePath (ref FilePath fileName)
		{
			try {
				// There are some situations where that may cause an exception.
				fileName = fileName.CanonicalPath;
			} catch (Exception) {
				// Fallback
				string fp = fileName;
				if (fp.Length > 0 && fp [fp.Length - 1] == Path.DirectorySeparatorChar)
					fileName = fp.TrimEnd (Path.DirectorySeparatorChar);
				if (fp.Length > 0 && fp [fp.Length - 1] == Path.AltDirectorySeparatorChar)
					fileName = fp.TrimEnd (Path.AltDirectorySeparatorChar);
			}
		}

		bool CheckCacheDirectoryIsCorrect (FilePath filename, FilePath candidate, out string result)
		{
			CanonicalizePath (ref filename);
			CanonicalizePath (ref candidate);
			lock (cacheDirectoryCache) {
				CacheDirectoryInfo info;
				if (!cacheDirectoryCache.TryGetValue (candidate, out info)) {
					var dataPath = candidate.Combine ("data.xml");

					try {
						if (!File.Exists (dataPath)) {
							result = null;
							return false;
						}
						using (var reader = XmlReader.Create (dataPath)) {
							while (reader.Read ()) {
								if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "File") {
									info.Version = reader.GetAttribute ("version");
									info.FileName = reader.GetAttribute ("name");
								}
							}
						}
						cacheDirectoryCache [candidate] = info;
					} catch (Exception e) {
						LoggingService.LogError ("Error while reading derived data file " + dataPath, e);
					}
				}
	
				if (info.Version == CurrentVersion && info.FileName == filename) {
					result = candidate;
					return true;
				}
	
				result = null;
				return false;
			}
		}

		string GetAssemblyCacheRoot (string filename)
		{
			string derivedDataPath = UserProfile.Current.CacheDir.Combine ("DerivedData");
			string name = Path.GetFileName (filename);
			return Path.Combine (derivedDataPath, name + "-" + GetStableHashCode(name).ToString ("x")); 	
		}

		/// <summary>
		/// Retrieves a hash code for the specified string that is stable across
		/// .NET upgrades.
		/// 
		/// Use this method instead of the normal <c>string.GetHashCode</c> if the hash code
		/// is persisted to disk.
		/// </summary>
		int GetStableHashCode(string text)
		{
			unchecked {
				int h = 0;
				foreach (char c in text) {
					h = (h << 5) - h + c;
				}
				return h;
			}
		}

		IEnumerable<string> GetPossibleCacheDirNames (string baseName)
		{
			int i = 0;
			while (i < 999999) {
				yield return Path.Combine (baseName, i.ToString ());
				i++;
			}
			throw new Exception ("Too many cache directories");
		}

		string EscapeToXml (string txt)
		{
			return new System.Xml.Linq.XText (txt).ToString ();
		}

		string CreateCacheDirectory (FilePath fileName)
		{
			CanonicalizePath (ref fileName);
			try {
				string cacheRoot = GetAssemblyCacheRoot (fileName);
				string cacheDir = GetPossibleCacheDirNames (cacheRoot).First (d => !Directory.Exists (d));

				Directory.CreateDirectory (cacheDir);

				File.WriteAllText (
					Path.Combine (cacheDir, "data.xml"),
					string.Format ("<DerivedData><File name=\"{0}\" version =\"{1}\"/></DerivedData>", EscapeToXml (fileName), CurrentVersion)
				);

				return cacheDir;
			} catch (Exception e) {
				LoggingService.LogError ("Error creating cache for " + fileName, e);
				return null;
			}
		}

		readonly FastSerializer sharedSerializer = new FastSerializer ();

		T DeserializeObject<T> (string path) where T : class
		{
			var t = Counters.ParserService.ObjectDeserialized.BeginTiming (path);
			try {
				using (var fs = new FileStream (path, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan)) {
					using (var reader = new BinaryReaderWith7BitEncodedInts (fs)) {
						lock (sharedSerializer) {
							return (T)sharedSerializer.Deserialize (reader);
						}
					}
				}
			} catch (Exception e) {
				LoggingService.LogError ("Error while trying to deserialize " + typeof(T).FullName + ". stack trace:" + Environment.StackTrace, e);
				return default(T);
			} finally {
				t.Dispose ();
			}
		}

		void SerializeObject (string path, object obj)
		{
			if (obj == null)
				throw new ArgumentNullException (nameof(obj));

			var t = Counters.ParserService.ObjectSerialized.BeginTiming (path);
			try {
				using (var fs = new FileStream (path, System.IO.FileMode.Create, FileAccess.Write)) {
					using (var writer = new BinaryWriterWith7BitEncodedInts (fs)) {
						lock (sharedSerializer) {
							sharedSerializer.Serialize (writer, obj);
						}
					}
				}
			} catch (Exception e) {
				Console.WriteLine ("-----------------Serialize stack trace:");
				Console.WriteLine (Environment.StackTrace);
				LoggingService.LogError ("Error while writing type system cache. (object:" + obj.GetType () + ")", e);
			} finally {
				t.Dispose ();
			}
		}

		/// <summary>
		/// Removes all cache directories which are older than 30 days.
		/// </summary>
		void CleanupCache ()
		{
			string derivedDataPath = UserProfile.Current.CacheDir.Combine ("DerivedData");
			IEnumerable<string> cacheDirectories;
			
			try {
				if (!Directory.Exists (derivedDataPath))
					return;
				cacheDirectories = Directory.EnumerateDirectories (derivedDataPath);
			} catch (Exception e) {
				LoggingService.LogError ("Error while getting derived data directories.", e);
				return;
			}
			var now = DateTime.Now;
			foreach (var cacheDirectory in cacheDirectories) {
				try {
					foreach (var subDir in Directory.EnumerateDirectories (cacheDirectory)) {
						try {
							var days = Math.Abs ((now - Directory.GetLastWriteTime (subDir)).TotalDays);
							if (days > 30)
								Directory.Delete (subDir, true);
						} catch (Exception e) {
							LoggingService.LogError ("Error while removing outdated cache " + subDir, e);
						}
					}
				} catch (Exception e) {
					LoggingService.LogError ("Error while getting cache directories " + cacheDirectory, e);
				}
			}
		}

		void RemoveCache (string cacheDir)
		{
			try {
				Directory.Delete (cacheDir, true);
			} catch (Exception e) {
				LoggingService.LogError ("Error while removing cache " + cacheDir, e);
			}
		}

		void TouchCache (string cacheDir)
		{
			try {
				Directory.SetLastWriteTime (cacheDir, DateTime.Now);
			} catch (Exception e) {
				LoggingService.LogError ("Error while touching cache directory " + cacheDir, e);
			}
		}

		void StoreExtensionObject (string cacheDir, object extensionObject)
		{
			if (cacheDir == null)
				throw new ArgumentNullException (nameof(cacheDir));
			if (extensionObject == null)
				throw new ArgumentNullException (nameof(extensionObject));
			var fileName = Path.GetTempFileName ();
			SerializeObject (fileName, extensionObject);
			var cacheFile = Path.Combine (cacheDir, extensionObject.GetType ().FullName + ".cache");

			try {
				if (File.Exists (cacheFile))
					File.Delete (cacheFile);
				File.Move (fileName, cacheFile);
			} catch (Exception e) {
				LoggingService.LogError ("Error whil saving cache " + cacheFile + " for extension object:" + extensionObject, e);
			}
		}

		#endregion
		internal void InformDocumentClose (Microsoft.CodeAnalysis.DocumentId analysisDocument, SourceTextContainer container)
		{
			foreach (var w in workspaces) {
				if (w.GetOpenDocumentIds (analysisDocument.ProjectId).Contains (analysisDocument))
					w.InformDocumentClose (analysisDocument, container); 
			}
		}

		public Microsoft.CodeAnalysis.ProjectId GetProjectId (MonoDevelop.Projects.Project project)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			foreach (var w in workspaces) {
				var projectId = w.GetProjectId (project);
				if (projectId != null) {
					return projectId;
				}
			}
			return null;
		}

		public Microsoft.CodeAnalysis.Document GetCodeAnalysisDocument (Microsoft.CodeAnalysis.DocumentId docId, CancellationToken cancellationToken = default (CancellationToken))
		{
			if (docId == null)
				throw new ArgumentNullException (nameof(docId));
			foreach (var w in workspaces) {
				var documentId = w.GetDocument (docId, cancellationToken);
				if (documentId != null) {
					return documentId;
				}
			}
			return null;
		}

		public MonoDevelop.Projects.Project GetMonoProject (Microsoft.CodeAnalysis.Project project)
		{
			if (project == null)
				throw new ArgumentNullException (nameof(project));
			foreach (var w in workspaces) {
				var documentId = w.GetMonoProject (project);
				if (documentId != null) {
					return documentId;
				}
			}
			return null;
		}


		public MonoDevelop.Projects.Project GetMonoProject (Microsoft.CodeAnalysis.DocumentId documentId)
		{
			foreach (var w in workspaces) {
				var doc = w.GetDocument (documentId);
				if (doc == null)
					continue;

				var p = doc.Project;
				if (p != null)
					return GetMonoProject (p);
			}
			return null;
		}

		object workspaceLoadLock = new object ();
		TaskCompletionSource<bool> workspaceLoadTaskSource;
		StatusBarIcon statusIcon = null;
		int workspacesLoading = 0;

		public static Func<Task> FreezeLoad = () => Task.CompletedTask;

		internal static async Task SafeFreezeLoad ()
		{
			try {
				await FreezeLoad ();
			} catch (Exception) {
				// Ignore exceptions, such as the task being cancelled. We want to freeze the load
				// whilst the NuGet restore is being run and continue after it has finished, cancelled,
				// or thrown an exception.
			}
		}

		public Task ProcessPendingLoadOperations ()
		{
			lock (workspaceLoadLock) {
				return workspaceLoadTaskSource?.Task ?? Task.CompletedTask;
			}
		}

		internal void BeginWorkspaceLoad ()
		{
			lock (workspaceLoadLock) {
				if (++workspacesLoading == 1) {
					workspaceLoadTaskSource = new TaskCompletionSource<bool> ();
					UpdateTypeInformationGatheringIcon ();
				}
			}
		}

		internal void EndWorkspaceLoad (Action callback = null)
		{
			TaskCompletionSource<bool> completedTask = null;

			lock (workspaceLoadLock) {
				if (--workspacesLoading == 0) {
					completedTask = workspaceLoadTaskSource;
					workspaceLoadTaskSource = null;
					UpdateTypeInformationGatheringIcon ();
				}
			}
			if (completedTask != null) {
				completedTask.SetResult (true);
				Runtime.RunInMainThread (() => {
					callback?.Invoke ();
				});
			}
		}

		void UpdateTypeInformationGatheringIcon ()
		{
			if (!IdeApp.IsInitialized)
				return;
			Runtime.RunInMainThread (() => {
				lock (workspaceLoadLock) {
					if (workspacesLoading > 0) {
						if (statusIcon == null) {
							statusIcon = IdeApp.Workbench?.StatusBar.ShowStatusIcon (ImageService.GetIcon (Gui.Stock.Parser));
							if (statusIcon != null)
								statusIcon.ToolTip = GettextCatalog.GetString ("Gathering class information");
						}
					} else {
						if (statusIcon != null) {
							statusIcon.Dispose ();
							statusIcon = null;
						}
					}
				}
			}).Ignore ();
		}
	}
}
