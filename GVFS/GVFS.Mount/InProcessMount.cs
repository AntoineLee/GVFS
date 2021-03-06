﻿using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Http;
using GVFS.Common.NamedPipes;
using GVFS.Common.Physical;
using GVFS.Common.Physical.FileSystem;
using GVFS.Common.Physical.Git;
using GVFS.Common.Tracing;
using GVFS.GVFlt;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace GVFS.Mount
{
    public class InProcessMount
    {
        // Tests show that 250 is the max supported pipe name length
        private const int MaxPipeNameLength = 250;
        private const int MutexMaxWaitTimeMS = 500;

        private readonly bool showDebugWindow;

        private GVFltCallbacks gvfltCallbacks;
        private GVFSEnlistment enlistment;
        private ITracer tracer;
        private GVFSGitObjects gitObjects;
        private GVFSLock gvfsLock;

        private MountState currentState;
        private HeartbeatThread heartbeat;
        private ManualResetEvent unmountEvent;

        private List<SafeFileHandle> folderLockHandles;

        public InProcessMount(ITracer tracer, GVFSEnlistment enlistment, bool showDebugWindow)
        {
            this.tracer = tracer;
            this.enlistment = enlistment;
            this.showDebugWindow = showDebugWindow;
            this.unmountEvent = new ManualResetEvent(false);
        }

        private enum MountState
        {
            Invalid = 0,

            Mounting,
            Ready,
            Unmounting,
            MountFailed
        }

        public void Mount(EventLevel verbosity, Keywords keywords)
        {
            this.currentState = MountState.Mounting;
            if (Environment.CurrentDirectory != this.enlistment.EnlistmentRoot)
            {
                Environment.CurrentDirectory = this.enlistment.EnlistmentRoot;
            }

            using (NamedPipeServer pipeServer = this.StartNamedPipe())
            {
                this.AcquireRepoMutex();

                GVFSContext context = this.CreateContext();

                this.ValidateMountPoints();
                this.UpdateHooks();
                this.SetVisualStudioRegistryKey();

                this.gvfsLock = context.Repository.GVFSLock;
                this.MountAndStartWorkingDirectoryCallbacks(context);

                Console.Title = "GVFS " + ProcessHelper.GetCurrentProcessVersion() + " - " + this.enlistment.EnlistmentRoot;

                this.tracer.RelatedEvent(
                    EventLevel.Critical,
                    "Mount",
                    new EventMetadata
                    {
                        { "Message", "Virtual repo is ready" },
                    });

                this.currentState = MountState.Ready;

                this.unmountEvent.WaitOne();
            }
        }

        private GVFSContext CreateContext()
        {
            PhysicalFileSystem fileSystem = new PhysicalFileSystem();
            GitRepo gitRepo = this.CreateOrReportAndExit(
                () => new GitRepo(
                    this.tracer,
                    this.enlistment,
                    fileSystem),
                "Failed to read git repo");
            return new GVFSContext(this.tracer, fileSystem, gitRepo, this.enlistment);
        }

        private void ValidateMountPoints()
        {
            DirectoryInfo workingDirectoryRootInfo = new DirectoryInfo(this.enlistment.WorkingDirectoryRoot);
            if (!workingDirectoryRootInfo.Exists)
            {
                this.FailMountAndExit("Failed to initialize file system callbacks. Directory \"{0}\" must exist.", this.enlistment.WorkingDirectoryRoot);
            }

            string dotGitPath = Path.Combine(this.enlistment.WorkingDirectoryRoot, GVFSConstants.DotGit.Root);
            DirectoryInfo dotGitPathInfo = new DirectoryInfo(dotGitPath);
            if (!dotGitPathInfo.Exists)
            {
                this.FailMountAndExit("Failed to mount. Directory \"{0}\" must exist.", dotGitPathInfo);
            }
        }

        private void UpdateHooks()
        {
            bool copyReadObjectHook = false;
            string enlistmentReadObjectHookPath = Path.Combine(this.enlistment.WorkingDirectoryRoot, GVFSConstants.DotGit.Hooks.ReadObjectPath + ".exe");
            string installedReadObjectHookPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), GVFSConstants.GVFSReadObjectHookExecutableName);

            if (!File.Exists(installedReadObjectHookPath))
            {
                this.FailMountAndExit(GVFSConstants.GVFSReadObjectHookExecutableName + " cannot be found at {0}", installedReadObjectHookPath);
            }

            if (!File.Exists(enlistmentReadObjectHookPath))
            {
                copyReadObjectHook = true;

                EventMetadata metadata = new EventMetadata();
                metadata.Add("Area", "Mount");
                metadata.Add("enlistmentReadObjectHookPath", enlistmentReadObjectHookPath);
                metadata.Add("installedReadObjectHookPath", installedReadObjectHookPath);
                metadata.Add("Message", GVFSConstants.DotGit.Hooks.ReadObjectName + " not found in enlistment, copying from installation folder");
                this.tracer.RelatedEvent(EventLevel.Warning, "ReadObjectMissingFromEnlistment", metadata);
            }
            else
            {
                try
                {
                    FileVersionInfo enlistmentVersion = FileVersionInfo.GetVersionInfo(enlistmentReadObjectHookPath);
                    FileVersionInfo installedVersion = FileVersionInfo.GetVersionInfo(installedReadObjectHookPath);
                    copyReadObjectHook = enlistmentVersion.FileVersion != installedVersion.FileVersion;
                }
                catch (Exception e)
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Area", "Mount");
                    metadata.Add("enlistmentReadObjectHookPath", enlistmentReadObjectHookPath);
                    metadata.Add("installedReadObjectHookPath", installedReadObjectHookPath);
                    metadata.Add("Exception", e.ToString());
                    metadata.Add("ErrorMessage", "Failed to compare " + GVFSConstants.DotGit.Hooks.ReadObjectName + " version");
                    this.tracer.RelatedError(metadata);
                    this.FailMountAndExit("Error comparing " + GVFSConstants.DotGit.Hooks.ReadObjectName + " versions,  run 'gvfs log' for details");
                }
            }

            if (copyReadObjectHook)
            {
                try
                {
                    File.Copy(installedReadObjectHookPath, enlistmentReadObjectHookPath, overwrite: true);
                }
                catch (Exception e)
                {
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Area", "Mount");
                    metadata.Add("enlistmentReadObjectHookPath", enlistmentReadObjectHookPath);
                    metadata.Add("installedReadObjectHookPath", installedReadObjectHookPath);
                    metadata.Add("Exception", e.ToString());
                    metadata.Add("ErrorMessage", "Failed to copy " + GVFSConstants.DotGit.Hooks.ReadObjectName + " to enlistment");
                    this.tracer.RelatedError(metadata);
                    this.FailMountAndExit("Error copying " + GVFSConstants.DotGit.Hooks.ReadObjectName + " to enlistment,  run 'gvfs log' for details");
                }
            }
        }

        private void SetVisualStudioRegistryKey()
        {
            const string GitBinPathEnd = "\\cmd\\git.exe";
            const string GitVSRegistryKeyName = "HKEY_CURRENT_USER\\Software\\Microsoft\\VSCommon\\15.0\\TeamFoundation\\GitSourceControl";
            const string GitVSRegistryValueName = "GitPath";

            if (!this.enlistment.GitBinPath.EndsWith(GitBinPathEnd))
            {
                this.tracer.RelatedError("Unable to configure Visual Studio’s GitSourceControl regkey because invalid git.exe path found: {0}", this.enlistment.GitBinPath);
                return;
            }

            string regKeyValue = this.enlistment.GitBinPath.Substring(0, this.enlistment.GitBinPath.Length - GitBinPathEnd.Length);
            Registry.SetValue(GitVSRegistryKeyName, GitVSRegistryValueName, regKeyValue);
        }

        private NamedPipeServer StartNamedPipe()
        {
            try
            {
                return NamedPipeServer.StartNewServer(this.enlistment.NamedPipeName, this.tracer, this.HandleRequest);
            }
            catch (PipeNameLengthException)
            {
                this.FailMountAndExit("Failed to create mount point. Mount path exceeds the maximum number of allowed characters");
                return null;
            }
        }

        private void FailMountAndExit(string error, params object[] args)
        {
            this.currentState = MountState.MountFailed;

            this.tracer.RelatedError(error, args);
            if (this.showDebugWindow)
            {
                Console.WriteLine("\nPress Enter to Exit");
                Console.ReadLine();
            }

            if (this.gvfltCallbacks != null)
            {
                this.gvfltCallbacks.Dispose();
                this.gvfltCallbacks = null;
            }

            Environment.Exit((int)ReturnCode.GenericError);
        }

        private void AcquireRepoMutex()
        {
            try
            {
                if (!this.enlistment.EnlistmentMutex.WaitOne(MutexMaxWaitTimeMS))
                {
                    this.FailMountAndExit("Error: GVFS is already mounted for this repo");
                }
            }
            catch (AbandonedMutexException)
            {
                // "The exception that is thrown when one thread acquires a Mutex object that another thread has abandoned by exiting without releasing it"
                // "The next thread to request ownership of the mutex can handle this exception and proceed"
                // https://msdn.microsoft.com/en-us/library/system.threading.abandonedmutexexception(v=vs.110).aspx
                //
                // If we catch AbandonedMutexException here it means that a previous instance of GVFS for this repo was not shut down gracefully.
                // Return true as catching this exception means that we have now acquired the mutex.
            }
            catch (Exception)
            {
                this.FailMountAndExit("Error: Failed to determine if repo is already mounted.");
            }
        }

        private T CreateOrReportAndExit<T>(Func<T> factory, string reportMessage)
        {
            try
            {
                return factory();
            }
            catch (Exception e)
            {
                this.FailMountAndExit(reportMessage + " " + e.ToString());
                throw;
            }
        }

        private void HandleRequest(ITracer tracer, string request, NamedPipeServer.Connection connection)
        {
            NamedPipeMessages.Message message = NamedPipeMessages.Message.FromString(request);

            switch (message.Header)
            {
                case NamedPipeMessages.GetStatus.Request:
                    this.HandleGetStatusRequest(connection);
                    break;

                case NamedPipeMessages.Unmount.Request:
                    this.HandleUnmountRequest(connection);
                    break;

                case NamedPipeMessages.AcquireLock.AcquireRequest:
                    this.HandleLockRequest(message.Body, connection);
                    break;

                case NamedPipeMessages.ReleaseLock.Request:
                    this.HandleReleaseLockRequest(message.Body, connection);
                    break;

                case NamedPipeMessages.DownloadObject.DownloadRequest:
                    this.HandleDownloadObjectRequest(message, connection);
                    break;

                default:
                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("Area", "Mount");
                    metadata.Add("Header", message.Header);
                    metadata.Add("ErrorMessage", "HandleRequest: Unknown request");
                    this.tracer.RelatedError(metadata);

                    connection.TrySendResponse(NamedPipeMessages.UnknownRequest);
                    break;
            }
        }

        private void HandleLockRequest(string messageBody, NamedPipeServer.Connection connection)
        {
            NamedPipeMessages.AcquireLock.Response response;
            NamedPipeMessages.LockData externalHolder;

            NamedPipeMessages.LockRequest request = new NamedPipeMessages.LockRequest(messageBody);
            NamedPipeMessages.LockData requester = request.RequestData;
            if (request == null)
            {
                response = new NamedPipeMessages.AcquireLock.Response(NamedPipeMessages.UnknownRequest, requester);
            }
            else if (this.currentState != MountState.Ready)
            {
                response = new NamedPipeMessages.AcquireLock.Response(NamedPipeMessages.AcquireLock.MountNotReadyResult);
            }
            else
            {
                if (this.gvfltCallbacks.IsReadyForExternalAcquireLockRequests())
                {
                    bool lockAcquired = this.gvfsLock.TryAcquireLock(requester, out externalHolder);

                    if (lockAcquired)
                    {
                        response = new NamedPipeMessages.AcquireLock.Response(NamedPipeMessages.AcquireLock.AcceptResult);
                    }
                    else if (externalHolder == null)
                    {
                        response = new NamedPipeMessages.AcquireLock.Response(NamedPipeMessages.AcquireLock.DenyGVFSResult);
                    }
                    else
                    {
                        response = new NamedPipeMessages.AcquireLock.Response(NamedPipeMessages.AcquireLock.DenyGitResult, externalHolder);
                    }
                }
                else
                {
                    response = new NamedPipeMessages.AcquireLock.Response(NamedPipeMessages.AcquireLock.DenyGVFSResult);
                }
            }

            connection.TrySendResponse(response.CreateMessage());
        }

        private void HandleReleaseLockRequest(string messageBody, NamedPipeServer.Connection connection)
        {
            NamedPipeMessages.LockRequest request = new NamedPipeMessages.LockRequest(messageBody);
            NamedPipeMessages.ReleaseLock.Response response = this.gvfltCallbacks.TryReleaseExternalLock(request.RequestData.PID);
            connection.TrySendResponse(response.CreateMessage());
        }

        private void HandleDownloadObjectRequest(NamedPipeMessages.Message message, NamedPipeServer.Connection connection)
        {
            NamedPipeMessages.DownloadObject.Response response;

            NamedPipeMessages.DownloadObject.Request request = new NamedPipeMessages.DownloadObject.Request(message);
            string objectSha = request.RequestSha;
            if (request == null)
            {
                response = new NamedPipeMessages.DownloadObject.Response(NamedPipeMessages.UnknownRequest);
            }
            else if (this.currentState != MountState.Ready)
            {
                response = new NamedPipeMessages.DownloadObject.Response(NamedPipeMessages.DownloadObject.MountNotReadyResult);
            }
            else
            {
                if (!GitHelper.IsValidFullSHA(objectSha))
                {
                    response = new NamedPipeMessages.DownloadObject.Response(NamedPipeMessages.DownloadObject.InvalidSHAResult);
                }
                else
                {
                    if (this.gitObjects.TryDownloadAndSaveObject(objectSha.Substring(0, 2), objectSha.Substring(2)))
                    {
                        response = new NamedPipeMessages.DownloadObject.Response(NamedPipeMessages.DownloadObject.SuccessResult);
                    }
                    else
                    {
                        response = new NamedPipeMessages.DownloadObject.Response(NamedPipeMessages.DownloadObject.DownloadFailed);
                    }
                }
            }

            connection.TrySendResponse(response.CreateMessage());
        }

        private void HandleGetStatusRequest(NamedPipeServer.Connection connection)
        {
            NamedPipeMessages.GetStatus.Response response = new NamedPipeMessages.GetStatus.Response();
            response.EnlistmentRoot = this.enlistment.EnlistmentRoot;
            response.RepoUrl = this.enlistment.RepoUrl;
            response.ObjectsUrl = this.enlistment.ObjectsEndpointUrl;
            response.LockStatus = this.gvfsLock != null ? this.gvfsLock.GetStatus() : "Unavailable";
            response.DiskLayoutVersion = RepoMetadata.GetCurrentDiskLayoutVersion();

            switch (this.currentState)
            {
                case MountState.Mounting:
                    response.MountStatus = NamedPipeMessages.GetStatus.Mounting;
                    break;

                case MountState.Ready:
                    response.MountStatus = NamedPipeMessages.GetStatus.Ready;
                    response.BackgroundOperationCount = this.gvfltCallbacks.GetBackgroundOperationCount();
                    break;

                case MountState.Unmounting:
                    response.MountStatus = NamedPipeMessages.GetStatus.Unmounting;
                    break;

                case MountState.MountFailed:
                    response.MountStatus = NamedPipeMessages.GetStatus.MountFailed;
                    break;

                default:
                    response.MountStatus = NamedPipeMessages.UnknownGVFSState;
                    break;
            }

            connection.TrySendResponse(response.ToJson());
        }

        private void HandleUnmountRequest(NamedPipeServer.Connection connection)
        {
            switch (this.currentState)
            {
                case MountState.Mounting:
                    connection.TrySendResponse(NamedPipeMessages.Unmount.NotMounted);
                    break;

                // Even if the previous mount failed, attempt to unmount anyway.  Otherwise the user has no
                // recourse but to kill the process.
                case MountState.MountFailed:
                    goto case MountState.Ready;

                case MountState.Ready:
                    this.currentState = MountState.Unmounting;

                    connection.TrySendResponse(NamedPipeMessages.Unmount.Acknowledged);
                    this.UnmountAndStopWorkingDirectoryCallbacks();
                    connection.TrySendResponse(NamedPipeMessages.Unmount.Completed);

                    this.unmountEvent.Set();
                    Environment.Exit((int)ReturnCode.Success);
                    break;

                case MountState.Unmounting:
                    connection.TrySendResponse(NamedPipeMessages.Unmount.AlreadyUnmounting);
                    break;

                default:
                    connection.TrySendResponse(NamedPipeMessages.UnknownGVFSState);
                    break;
            }
        }

        private void AcquireFolderLocks(GVFSContext context)
        {
            this.folderLockHandles = new List<SafeFileHandle>();
            this.folderLockHandles.Add(context.FileSystem.LockDirectory(context.Enlistment.DotGVFSRoot));
        }

        private void ReleaseFolderLocks()
        {
            foreach (SafeFileHandle folderHandle in this.folderLockHandles)
            {
                folderHandle.Dispose();
            }
        }

        private void MountAndStartWorkingDirectoryCallbacks(GVFSContext context)
        {
            string error;
            if (!context.Enlistment.Authentication.TryRefreshCredentials(context.Tracer, out error))
            {
                this.FailMountAndExit("Failed to obtain git credentials: " + error);
            }

            // Checking the disk layout version is done before this point in GVFS.CommandLine.MountVerb.PreExecute
            RepoMetadata repoMetadata = new RepoMetadata(this.enlistment.DotGVFSRoot);
            GitObjectsHttpRequestor objectRequestor = new GitObjectsHttpRequestor(context.Tracer, context.Enlistment);
            this.gitObjects = new GVFSGitObjects(context, objectRequestor);
            this.gvfltCallbacks = this.CreateOrReportAndExit(() => new GVFltCallbacks(context, this.gitObjects, repoMetadata), "Failed to create src folder callbacks");

            int persistedVersion;
            if (!repoMetadata.TryGetOnDiskLayoutVersion(out persistedVersion, out error))
            {
                this.FailMountAndExit("Error: {0}", error);
            }

            try
            {
                if (!this.gvfltCallbacks.TryStart(out error))
                {
                    this.FailMountAndExit("Error: {0}. \r\nPlease confirm that gvfs clone completed without error.", error);
                }
            }
            catch (Exception e)
            {
                this.FailMountAndExit("Failed to initialize src folder callbacks. {0}", e.ToString());
            }

            try
            {
                repoMetadata.SaveCurrentDiskLayoutVersion();
            }
            catch (Exception ex)
            {
                this.FailMountAndExit("Failed to update repo disk layout version: {0}", ex.ToString());
            }

            this.AcquireFolderLocks(context);

            this.heartbeat = new HeartbeatThread(this.tracer, this.gvfltCallbacks);
            this.heartbeat.Start();
        }

        private void UnmountAndStopWorkingDirectoryCallbacks()
        {
            this.ReleaseFolderLocks();

            if (this.heartbeat != null)
            {
                this.heartbeat.Stop();
                this.heartbeat = null;
            }

            if (this.gvfltCallbacks != null)
            {
                this.gvfltCallbacks.Stop();
                this.gvfltCallbacks.Dispose();
                this.gvfltCallbacks = null;
            }
        }
    }
}