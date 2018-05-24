﻿using System;
using System.Linq;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GitHub.Models;
using GitHub.Logging;
using GitHub.Primitives;
using Serilog;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace GitHub.Services
{
    /// <summary>
    /// This implementation listenes to IGitExt for ActiveRepositories property change events and fires
    /// <see cref="PropertyChanged"/> and <see cref="StatusChanged"/> events when appropriate.
    /// </summary>
    /// <remarks>
    /// A <see cref="PropertyChanged"/> is fired when a solution is opened in a new repository (or not in a repository).
    /// A <see cref="StatusChanged"/> event is only fired when the current branch, head SHA or tracked SHA changes (not
    /// on every IGitExt property change event). <see cref="ActiveRepository"/> contains the active repository or null
    /// if a solution is opened that isn't in a repository. No events are fired when the same solution is unloaded then
    /// reloaded (e.g. when a .sln file is touched).
    /// </remarks>
    [Export(typeof(ITeamExplorerContext))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class TeamExplorerContext : ITeamExplorerContext
    {
        static ILogger log = LogManager.ForContext<TeamExplorerContext>();

        readonly AsyncLazy<DTE> dteAsync;
        readonly IVSGitExt gitExt;
        readonly IPullRequestService pullRequestService;

        string solutionPath;
        string repositoryPath;
        UriString cloneUrl;
        string branchName;
        string headSha;
        string trackedSha;
        Tuple<string, int> pullRequest;

        ILocalRepositoryModel repositoryModel;

        [ImportingConstructor]
        TeamExplorerContext(
            IVSGitExt gitExt,
            [Import(typeof(SVsServiceProvider))] IServiceProvider sp,
            IPullRequestService pullRequestService) : this(
                gitExt,
                new AsyncLazy<DTE>(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return (DTE)sp.GetService(typeof(DTE));
                }),
                pullRequestService,
                ThreadHelper.JoinableTaskContext)
        {
        }

        public TeamExplorerContext(
            IVSGitExt gitExt,
            AsyncLazy<DTE> dteAsync,
            IPullRequestService pullRequestService,
            JoinableTaskContext joinableTaskContext)
        {
            JoinableTaskCollection = joinableTaskContext.CreateCollection();
            JoinableTaskCollection.DisplayName = nameof(TeamExplorerContext);
            JoinableTaskFactory = joinableTaskContext.CreateFactory(JoinableTaskCollection);
            
            this.gitExt = gitExt;
            this.dteAsync = dteAsync;
            this.pullRequestService = pullRequestService;

            StartRefresh();
            gitExt.ActiveRepositoriesChanged += Refresh;
        }

        void StartRefresh() => JoinableTaskFactory.Context.Factory.RunAsync(QueueRefreshAsync).Task.FileAndForget(log);
        void Refresh() => JoinableTaskFactory.Context.Factory.Run(QueueRefreshAsync);

        async Task QueueRefreshAsync()
        {
            await JoinableTaskCollection.JoinTillEmptyAsync();
            await JoinableTaskFactory.RunAsync(RefreshAsync);
        }

        async Task RefreshAsync()
        {
            try
            {
                await TaskScheduler.Default; // switch to threadpool

                var repo = gitExt.ActiveRepositories?.FirstOrDefault();
                string newSolutionPath = await GetSolutionPath();
                if (repo == null && newSolutionPath == solutionPath)
                {
                    // Ignore when ActiveRepositories is empty and solution hasn't changed.
                    // https://github.com/github/VisualStudio/issues/1421
                    log.Debug("Ignoring no ActiveRepository when solution hasn't changed");
                }
                else
                {
                    var newRepositoryPath = repo?.LocalPath;
                    var newCloneUrl = repo?.CloneUrl;
                    var newBranchName = repo?.CurrentBranch?.Name;
                    var newHeadSha = repo?.CurrentBranch?.Sha;
                    var newTrackedSha = repo?.CurrentBranch?.TrackedSha;
                    var newPullRequest = repo != null ? await pullRequestService.GetPullRequestForCurrentBranch(repo) : null;

                    if (newRepositoryPath != repositoryPath)
                    {
                        log.Debug("ActiveRepository changed to {CloneUrl} @ {Path}", repo?.CloneUrl, newRepositoryPath);
                        ActiveRepository = repo;
                    }
                    else if (newCloneUrl != cloneUrl)
                    {
                        log.Debug("ActiveRepository changed to {CloneUrl} @ {Path}", repo?.CloneUrl, newRepositoryPath);
                        ActiveRepository = repo;
                    }
                    else if (newBranchName != branchName)
                    {
                        log.Debug("Fire StatusChanged event when BranchName changes for ActiveRepository");
                        StatusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (newHeadSha != headSha)
                    {
                        log.Debug("Fire StatusChanged event when HeadSha changes for ActiveRepository");
                        StatusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (newTrackedSha != trackedSha)
                    {
                        log.Debug("Fire StatusChanged event when TrackedSha changes for ActiveRepository");
                        StatusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (newPullRequest != pullRequest)
                    {
                        log.Debug("Fire StatusChanged event when PullRequest changes for ActiveRepository");
                        StatusChanged?.Invoke(this, EventArgs.Empty);
                    }

                    repositoryPath = newRepositoryPath;
                    cloneUrl = newCloneUrl;
                    branchName = newBranchName;
                    headSha = newHeadSha;
                    solutionPath = newSolutionPath;
                    trackedSha = newTrackedSha;
                    pullRequest = newPullRequest;
                }
            }
            catch (Exception e)
            {
                log.Error(e, "Refreshing active repository");
            }
        }

        async Task<string> GetSolutionPath()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await dteAsync.GetValueAsync();
            return dte.Solution?.FullName;
        }

        /// <summary>
        /// The active repository or null if not in a repository.
        /// </summary>
        public ILocalRepositoryModel ActiveRepository
        {
            get
            {
                return repositoryModel;
            }

            private set
            {
                if (value != repositoryModel)
                {
                    repositoryModel = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRepository)));
                }
            }
        }

        /// <summary>
        /// Fired when a solution is opened in a new repository (or that isn't in a repository).
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Fired when the current branch, head SHA or tracked SHA changes.
        /// </summary>
        public event EventHandler StatusChanged;

        public JoinableTaskCollection JoinableTaskCollection { get; }
        JoinableTaskFactory JoinableTaskFactory { get; }
    }
}
