﻿using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.PackageManagement;
using NuGet.ProjectManagement;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EnvDTEProject = EnvDTE.Project;

namespace NuGet.PackageManagement.VisualStudio
{
    [PartCreationPolicy(CreationPolicy.Shared)]
    [Export(typeof(ISolutionManager))]
    public class VSSolutionManager : ISolutionManager, IVsSelectionEvents
    {
        private readonly DTE _dte;
        private readonly SolutionEvents _solutionEvents;
        private readonly IVsMonitorSelection _vsMonitorSelection;
        private readonly uint _solutionLoadedUICookie;
        private readonly IVsSolution _vsSolution;
        private bool _initNeeded;
        private NuGetAndEnvDTEProjectCache NuGetAndEnvDTEProjectCache { get; set; }

        private ManualResetEvent _initFinished;

        public VSSolutionManager()
            : this(
                ServiceLocator.GetInstance<DTE>(),
                ServiceLocator.GetGlobalService<SVsSolution, IVsSolution>(),
                ServiceLocator.GetGlobalService<SVsShellMonitorSelection, IVsMonitorSelection>())
        {
        }

        public VSSolutionManager(DTE dte, IVsSolution vsSolution, IVsMonitorSelection vsMonitorSelection)
        {
            if (dte == null)
            {
                throw new ArgumentNullException("dte");
            }

            _initNeeded = true;
            _dte = dte;
            _vsSolution = vsSolution;
            _vsMonitorSelection = vsMonitorSelection;

            // Keep a reference to SolutionEvents so that it doesn't get GC'ed. Otherwise, we won't receive events.
            _solutionEvents = _dte.Events.SolutionEvents;

            // can be null in unit tests
            if (vsMonitorSelection != null)
            {
                Guid solutionLoadedGuid = VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_guid;
                _vsMonitorSelection.GetCmdUIContextCookie(ref solutionLoadedGuid, out _solutionLoadedUICookie);

                uint cookie;
                int hr = _vsMonitorSelection.AdviseSelectionEvents(this, out cookie);
                ErrorHandler.ThrowOnFailure(hr);
            }

            _solutionEvents.BeforeClosing += OnBeforeClosing;
            _solutionEvents.AfterClosing += OnAfterClosing;
            _solutionEvents.ProjectAdded += OnEnvDTEProjectAdded;
            _solutionEvents.ProjectRemoved += OnEnvDTEProjectRemoved;
            _solutionEvents.ProjectRenamed += OnEnvDTEProjectRenamed;

            // Run the init on another thread to avoid an endless loop of SolutionManager -> Project System -> VSPackageManager -> SolutionManager            
            // TODO: fix the cyclic reference problem; then remove _initFinished.
            _initFinished = new ManualResetEvent(false);
            ThreadPool.QueueUserWorkItem(new WaitCallback(Init));
        }

        public NuGetProject DefaultNuGetProject
        {
            get
            {
                if (String.IsNullOrEmpty(DefaultNuGetProjectName))
                {
                    return null;
                }
                NuGetProject defaultNuGetProject;
                NuGetAndEnvDTEProjectCache.TryGetNuGetProject(DefaultNuGetProjectName, out defaultNuGetProject);
                return defaultNuGetProject;
            }
        }

        public string DefaultNuGetProjectName
        {
            get;
            set;
        }

        public NuGetProject GetNuGetProject(string nuGetProjectSafeName)
        {
            // wait for Init() to finish so that NuGetAndEnvDTEProjectCache is created.
            _initFinished.WaitOne();

            NuGetProject nuGetProject;
            NuGetAndEnvDTEProjectCache.TryGetNuGetProject(nuGetProjectSafeName, out nuGetProject);
            return nuGetProject;
        }

        public string GetNuGetProjectSafeName(NuGetProject nuGetProject)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<NuGetProject> GetNuGetProjects()
        {
            if(IsSolutionOpen)
            {
                EnsureNuGetAndEnvDTEProjectCache();
                return NuGetAndEnvDTEProjectCache.GetNuGetProjects();
            }

            return Enumerable.Empty<NuGetProject>();
        }

        internal IEnumerable<EnvDTEProject> GetEnvDTEProjects()
        {
            if(IsSolutionOpen)
            {
                EnsureNuGetAndEnvDTEProjectCache();
                return NuGetAndEnvDTEProjectCache.GetEnvDTEProjects();
            }

            return Enumerable.Empty<EnvDTEProject>();
        }

        public bool IsSolutionOpen
        {
            get
            {
                return _dte != null &&
                       _dte.Solution != null &&
                       _dte.Solution.IsOpen &&
                       !IsSolutionSavedAsRequired();
            }
        }

        public event EventHandler<NuGetProjectEventArgs> NuGetProjectAdded;
        public event EventHandler<NuGetProjectEventArgs> NuGetProjectRemoved;
        public event EventHandler<NuGetProjectEventArgs> NuGetProjectRenamed;

        public event EventHandler SolutionClosed;

        public event EventHandler SolutionClosing;

        public string SolutionDirectory
        {
            get
            {
                if (!IsSolutionOpen)
                {
                    return null;
                }

                string solutionFilePath = GetSolutionFilePath();

                if (String.IsNullOrEmpty(solutionFilePath))
                {
                    return null;
                }
                return Path.GetDirectoryName(solutionFilePath);
            }
        }

        private string GetSolutionFilePath()
        {
            // Use .Properties.Item("Path") instead of .FullName because .FullName might not be
            // available if the solution is just being created
            string solutionFilePath = null;

            Property property = _dte.Solution.Properties.Item("Path");
            if (property == null)
            {
                return null;
            }
            try
            {
                // When using a temporary solution, (such as by saying File -> New File), querying this value throws.
                // Since we wouldn't be able to do manage any packages at this point, we return null. Consumers of this property typically 
                // use a String.IsNullOrEmpty check either way, so it's alright.
                solutionFilePath = (string)property.Value;
            }
            catch (COMException)
            {
                return null;
            }

            return solutionFilePath;
        }

        public event EventHandler SolutionOpened;

        /// <summary>
        /// Checks whether the current solution is saved to disk, as opposed to be in memory.
        /// </summary>
        private bool IsSolutionSavedAsRequired()
        {
            // Check if user is doing File - New File without saving the solution.
            object value;
            _vsSolution.GetProperty((int)(__VSPROPID.VSPROPID_IsSolutionSaveAsRequired), out value);
            if ((bool)value)
            {
                return true;
            }

            // Check if user unchecks the "Tools - Options - Project & Soltuions - Save new projects when created" option
            _vsSolution.GetProperty((int)(__VSPROPID2.VSPROPID_DeferredSaveSolution), out value);
            return (bool)value;
        }

        /// <summary>
        /// Invokes the action on the UI thread if one exists.
        /// </summary>
        private void InvokeOnUIThread(Action action)
        {
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        }

        private void OnSolutionOpened()
        {
            // we can skip the init if this has already been called
            _initNeeded = false;

            // although the SolutionOpened event fires, the solution may be only in memory (e.g. when
            // doing File - New File). In that case, we don't want to act on the event.
            if (!IsSolutionOpen)
            {
                return;
            }

            EnsureNuGetAndEnvDTEProjectCache();
            SetDefaultProjectName();
            if (SolutionOpened != null)
            {
                SolutionOpened(this, EventArgs.Empty);
            }
        }

        private void OnAfterClosing()
        {
            if (SolutionClosed != null)
            {
                SolutionClosed(this, EventArgs.Empty);
            }
        }

        private void OnBeforeClosing()
        {
            DefaultNuGetProjectName = null;
            NuGetAndEnvDTEProjectCache.Clear();
            NuGetAndEnvDTEProjectCache = null;
            if (SolutionClosing != null)
            {
                SolutionClosing(this, EventArgs.Empty);
            }
        }

        private void OnEnvDTEProjectRenamed(EnvDTEProject envDTEProject, string oldName)
        {
            if (!String.IsNullOrEmpty(oldName))
            {
                EnsureNuGetAndEnvDTEProjectCache();

                if(EnvDTEProjectUtility.IsSupported(envDTEProject))
                {
                    RemoveEnvDTEProjectFromCache(oldName);
                    AddEnvDTEProjectToCache(envDTEProject);
                    NuGetProject nuGetProject;
                    NuGetAndEnvDTEProjectCache.TryGetNuGetProject(envDTEProject.Name, out nuGetProject);

                    if (NuGetProjectRenamed != null)
                    {
                        NuGetProjectRenamed(this, new NuGetProjectEventArgs(nuGetProject));
                    }
                }
            }
        }

        private void OnEnvDTEProjectRemoved(EnvDTEProject envDTEProject)
        {
            RemoveEnvDTEProjectFromCache(envDTEProject.FullName);
            NuGetProject nuGetProject;
            NuGetAndEnvDTEProjectCache.TryGetNuGetProject(envDTEProject.Name, out nuGetProject);

            if (NuGetProjectRemoved != null)
            {
                NuGetProjectRemoved(this, new NuGetProjectEventArgs(nuGetProject));
            }
        }

        private void OnEnvDTEProjectAdded(EnvDTEProject envDTEProject)
        {
            if (EnvDTEProjectUtility.IsSupported(envDTEProject) && !EnvDTEProjectUtility.IsParentProjectExplicitlyUnsupported(envDTEProject))
            {
                EnsureNuGetAndEnvDTEProjectCache();
                AddEnvDTEProjectToCache(envDTEProject);
                NuGetProject nuGetProject;
                NuGetAndEnvDTEProjectCache.TryGetNuGetProject(envDTEProject.Name, out nuGetProject);

                if (NuGetProjectAdded != null)
                {
                    NuGetProjectAdded(this, new NuGetProjectEventArgs(nuGetProject));
                }
            }
        }

        private void SetDefaultProjectName()
        {
            // when a new solution opens, we set its startup project as the default project in NuGet Console
            var solutionBuild = (SolutionBuild2)_dte.Solution.SolutionBuild;
            if (solutionBuild.StartupProjects != null)
            {
                IEnumerable<object> startupProjects = (IEnumerable<object>)solutionBuild.StartupProjects;
                string startupProjectName = startupProjects.Cast<string>().FirstOrDefault();
                if (!String.IsNullOrEmpty(startupProjectName))
                {
                    EnvDTEProjectName envDTEProjectName;
                    if (NuGetAndEnvDTEProjectCache.TryGetNuGetProjectName(startupProjectName, out envDTEProjectName))
                    {
                        DefaultNuGetProjectName = NuGetAndEnvDTEProjectCache.IsAmbiguous(envDTEProjectName.ShortName) ?
                                             envDTEProjectName.CustomUniqueName :
                                             envDTEProjectName.ShortName;
                    }
                }
            }
        }

        private void EnsureNuGetAndEnvDTEProjectCache()
        {
            if (IsSolutionOpen && NuGetAndEnvDTEProjectCache == null)
            {
                NuGetAndEnvDTEProjectCache = new NuGetAndEnvDTEProjectCache(this);

                var allEnvDTEProjects = EnvDTESolutionUtility.GetAllEnvDTEProjects(_dte.Solution);
                foreach (EnvDTEProject envDTEProject in allEnvDTEProjects)
                {
                    AddEnvDTEProjectToCache(envDTEProject);
                }
            }
        }

        private void AddEnvDTEProjectToCache(EnvDTEProject envDTEProject)
        {
            if (!EnvDTEProjectUtility.IsSupported(envDTEProject))
            {
                return;
            }
            EnvDTEProjectName oldEnvDTEProjectName;
            NuGetAndEnvDTEProjectCache.TryGetProjectNameByShortName(EnvDTEProjectUtility.GetName(envDTEProject), out oldEnvDTEProjectName);

            EnvDTEProjectName newEnvDTEProjectName = NuGetAndEnvDTEProjectCache.AddProject(envDTEProject);

            if (String.IsNullOrEmpty(DefaultNuGetProjectName) ||
                newEnvDTEProjectName.ShortName.Equals(DefaultNuGetProjectName, StringComparison.OrdinalIgnoreCase))
            {
                DefaultNuGetProjectName = oldEnvDTEProjectName != null ?
                                     oldEnvDTEProjectName.CustomUniqueName :
                                     newEnvDTEProjectName.ShortName;
            }
        }

        private void RemoveEnvDTEProjectFromCache(string name)
        {
            // Do nothing if the cache hasn't been set up
            if (NuGetAndEnvDTEProjectCache == null)
            {
                return;
            }

            EnvDTEProjectName envDTEProjectName;
            NuGetAndEnvDTEProjectCache.TryGetNuGetProjectName(name, out envDTEProjectName);

            // Remove the project from the cache
            NuGetAndEnvDTEProjectCache.RemoveProject(name);

            if (!NuGetAndEnvDTEProjectCache.Contains(DefaultNuGetProjectName))
            {
                DefaultNuGetProjectName = null;
            }

            // for LightSwitch project, the main project is not added to _projectCache, but it is called on removal. 
            // in that case, projectName is null.
            if (envDTEProjectName != null &&
                envDTEProjectName.CustomUniqueName.Equals(DefaultNuGetProjectName, StringComparison.OrdinalIgnoreCase) &&
                !NuGetAndEnvDTEProjectCache.IsAmbiguous(envDTEProjectName.ShortName))
            {
                DefaultNuGetProjectName = envDTEProjectName.ShortName;
            }
        }

        private void Init(object state)
        {
            try
            {
                if (_initNeeded && _dte.Solution.IsOpen)
                {
                    OnSolutionOpened();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                // siginal that Init is finished
                _initFinished.Set();
            }
        }

        // REVIEW: This might be inefficient, see what we can do with caching projects until references change
        internal IEnumerable<EnvDTEProject> GetDependentEnvDTEProjects(EnvDTEProject envDTEProject)
        {
            if (envDTEProject == null)
            {
                throw new ArgumentNullException("project");
            }

            var dependentProjects = new Dictionary<string, List<Project>>();

            // Get all of the projects in the solution and build the reverse graph. i.e.
            // if A has a project reference to B (A -> B) the this will return B -> A
            // We need to run this on the ui thread so that it doesn't freeze for websites. Since there might be a 
            // large number of references.           
            ThreadHelper.Generic.Invoke(() =>
            {
                foreach (EnvDTEProject envDTEProj in GetEnvDTEProjects())
                {
                    if (EnvDTEProjectUtility.SupportsReferences(envDTEProj))
                    {
                        foreach (var referencedProject in EnvDTEProjectUtility.GetReferencedProjects(envDTEProj))
                        {
                            AddDependentProject(dependentProjects, referencedProject, envDTEProject);
                        }
                    }
                }
            });

            List<Project> dependents;
            if (dependentProjects.TryGetValue(EnvDTEProjectUtility.GetUniqueName(envDTEProject), out dependents))
            {
                return dependents;
            }

            return Enumerable.Empty<EnvDTEProject>();
        }

        private static void AddDependentProject(IDictionary<string, List<EnvDTEProject>> dependentEnvDTEProjectDictionary,
            EnvDTEProject envDTEProject, EnvDTEProject dependentEnvDTEProject)
        {
            string uniqueName = EnvDTEProjectUtility.GetUniqueName(envDTEProject);

            List<EnvDTEProject> dependentEnvDTEProjects;
            if (!dependentEnvDTEProjectDictionary.TryGetValue(uniqueName, out dependentEnvDTEProjects))
            {
                dependentEnvDTEProjects = new List<EnvDTEProject>();
                dependentEnvDTEProjectDictionary[uniqueName] = dependentEnvDTEProjects;
            }
            dependentEnvDTEProjects.Add(dependentEnvDTEProject);
        }

        #region IVsSelectionEvents implementation

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive)
        {
            if (dwCmdUICookie == _solutionLoadedUICookie && fActive == 1)
            {
                OnSolutionOpened();
                // We must call DeleteMarkedPackageDirectories outside of OnSolutionOpened, because OnSolutionOpened might be called in the constructor
                // and DeleteOnRestartManager requires VsFileSystemProvider and RepositorySetings which both have dependencies on SolutionManager.
                // In practice, this code gets executed even when a solution is opened directly during Visual Studio startup.
                //DeleteOnRestartManager.Value.DeleteMarkedPackageDirectories();
            }

            return VSConstants.S_OK;
        }

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            return VSConstants.S_OK;
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld, IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
        {
            return VSConstants.S_OK;
        }

        #endregion 
    

        public INuGetProjectContext NuGetProjectContext
        {
            get;
            set;
        }
    }
}
