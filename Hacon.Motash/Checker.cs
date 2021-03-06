﻿using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Principal;
using System.ServiceProcess;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using wts = Microsoft.Win32.TaskScheduler;
using Hacon.Lib;

namespace Hacon.Motash
{
    /// <summary>
    /// Class to check the tasks
    /// </summary>
    public class Checker
    {
        #region Properties and members
  
        const int _appErrorId = -10556;
        const int _appWarningId = -10557;

        [ImportMany(typeof(INotifier))]
        private List<INotifier> _notifiers { get; set; }

        /// <summary>
        /// Use this instead of a hard-coded string
        /// </summary>
        public string AppName
        {
            get
            {
                return "Motash";
            }
        }

        /// <summary>
        /// Constructor, sets the LastCheck time to 1970
        /// </summary>
        public Checker()
        {
            LastCheck = new DateTime(1970, 1, 1);
        }

        /// <summary>
        /// Exposes a list of failures
        /// </summary>
        public List<Failure> Failures = new List<Failure>();

        /// <summary>
        /// True if there was a problem with setting up the checker object.
        /// </summary>
        public bool WeHaveASetupProblem { get; private set; }

        private bool? _checkRootTasks;

        /// <summary>
        /// Should we check tasks in the root, read from config file, default is false
        /// </summary>
        public bool CheckRootTasks 
        {
            get
            {
                if (_checkRootTasks == null)
                {
                    _checkRootTasks = bool.Parse(Config.GetApplicationSettingValue("CheckRootTasks", "false"));
                }
                return _checkRootTasks.Value;
            }
            set
            {
                _checkRootTasks = value;
            }
        }

        /// <summary>
        /// Last time we checked
        /// </summary>
        public DateTime LastCheck;

        string _rootFolderPattern = string.Empty;
        /// <summary>
        /// The regular expression to match the top level folder against
        /// </summary>
        /// <remarks>By default this comes from the app.config</remarks>
        public string RootFolderPattern
        {
            get
            {
                if (_rootFolderPattern == "")
                {
                    _rootFolderPattern = Config.GetApplicationSettingValue("RootFolderPattern", "");
                }
                return _rootFolderPattern;
            }
            set
            {
                _rootFolderPattern = value;
            }
        } 
        #endregion

        /// <summary>
        /// Runs the actual checks, sets the Problem count and string
        /// </summary>
        /// <returns></returns>
        public int Check()
        {
            // check for the correct setup, if one of the checks fails, exit right away.
            // with the new wrapper, it seems to work without being an admin
       //     if (!IsAdmin()) return SetProblem(AppName + " does not run as an administrator. This is required");
            if (Environment.OSVersion.Version.Major < 6) return SetProblem("Windows Vista or newer is required");
            if (!IsServiceRunning()) return SetProblem("The Task Scheduler service is not running");
            if (RootFolderPattern == "") return SetProblem("No RootFolderPattern set, check your config file.");

            try
            {
                using (wts.TaskService ts = new wts.TaskService())
                {
                    wts.TaskFolder root = ts.RootFolder;

                    if (CheckRootTasks)
                    {
                        CheckTasks(root);
                    }

                    ProcessFolder(root, RootFolderPattern);
                }
            }
            catch (Exception ex)
            {
                Exceptions.Log(ex);
                Failures.Add(new Failure
                {
                    Name = "Application Exception",
                    Path = ex.Message,
                    LastRun = DateTime.Now,
                    Result = _appErrorId,
                });
            }

            // return the number of problems found
            return Failures.Count;
        }

        /// <summary>
        /// Runs all available notifies.
        /// </summary>
        /// <returns>Number of notifiers executed</returns>
        public int Notify()
        {
            int notifierCount = 0;

            if (Failures.Count > 0)
            {
                try
                {
                    AggregateCatalog cat = new AggregateCatalog();
                    cat.Catalogs.Add(new DirectoryCatalog("."));
                    CompositionContainer contnr = new CompositionContainer(cat);

                    contnr.ComposeParts(this);

                    // loop through them
                    foreach (INotifier noti in this._notifiers)
                    {
                        noti.Send(Failures);
                        notifierCount++;
                    }
                }
                catch (Exception ex)
                {
                    Exceptions.Log(ex);
                }
            }

            return notifierCount;
        }

        #region Private Helper

        private void ProcessFolder(wts.TaskFolder folder, string filter)
        {
            foreach (wts.TaskFolder subFolder in folder.SubFolders)
            {
                if (filter != "")
                {
                    if (!Regex.IsMatch(subFolder.Name, filter, RegexOptions.IgnoreCase)) continue;
                }

                CheckTasks(subFolder);
                ProcessFolder(subFolder, "");
            }
        }


        private int SetProblem(string problem)
        {
            WeHaveASetupProblem = true;
            Failures.Add(new Failure
            {
                Name = "Application Warning",
                Path = problem,
                LastRun = DateTime.Now,
                Result = _appWarningId,
            });

            return 1;
        }

        private bool IsAdmin()
        {
            WindowsPrincipal user = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return user.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private bool IsServiceRunning()
        {
            ServiceController sc = new ServiceController("Schedule");
            if (sc.Status == ServiceControllerStatus.Running)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static string FailuresAsText(List<Failure> failures, string format = "{0} ({1}) at: {2}")
        {
            StringBuilder text = new StringBuilder();

            foreach (Failure failure in failures)
            {
                text.AppendLine(string.Format(format, failure.Path,
                    failure.Result.ToString(),failure.LastRun.ToString("dd-MMM-yyyy HH:mm:ss")));
            }

            return text.ToString();
        }

        /// <summary>
        /// Checks tasks in one folder
        /// </summary>
        /// <param name="ts"></param>
        /// <param name="path"></param>
        private void CheckTasks(wts.TaskFolder folder)
        {
            foreach (wts.Task task in folder.Tasks)
            {
                try
                {
                    // if task is disabled, ignore it.
                    if (task.State == wts.TaskState.Disabled)
                    {
                        continue;
                    }
                    // if tasks is currently running, ignore it.
                    if (task.State == wts.TaskState.Running)
                    {
                        continue;
                    }

                    // if the task ran last before our last check, we already checked it
                    // so ignore it this time
                    if (task.LastRunTime < LastCheck)
                    {
                        continue;
                    }

                    // get a list of allowed exit codes for this tasks, if no custom ones found
                    // only 0 is allowed
                    List<int> allowedResultCodes = GetAllowedResults(task.Definition.RegistrationInfo.Description + "");

                    const int SCHED_S_TASK_RUNNING = 267009;
                    // also allow exit code 0x00041301 or 267009 which is SCHED_S_TASK_RUNNING
                    // why this is not covered by wts.TaskState.Running above, I don't know
                    allowedResultCodes.Add(SCHED_S_TASK_RUNNING);

                    // check whether we have an exit code that is not allowed
                    if (!allowedResultCodes.Contains(task.LastTaskResult))
                    {
                        Failures.Add(new Failure 
                            { Name = task.Name, 
                              Path = task.Path, 
                              LastRun = task.LastRunTime, 
                              Result = task.LastTaskResult });
                    }                    
                }
                catch (Exception ex)
                {
                    // in any error case, also add a problem
                    Exceptions.Log(ex);
                    Failures.Add(new Failure
                    {
                        Name = task.Name,
                        Path = task.Path,
                        LastRun = DateTime.Now,
                        Result = task.LastTaskResult
                    });
                }
            }
        } 

        /// <summary>
        /// Gets a list of allowed exit codes
        /// </summary>
        /// <param name="description">The string to parse</param>
        /// <returns>We are looking for curly brackets with just comma separated integers in between.</returns>
        private List<int> GetAllowedResults(string description)
        {
            List<int> results = new List<int>();

            RegexOptions options = RegexOptions.None;
            Regex rxFind = new Regex(@"{[0-9,]+}", options);
            Regex rxSplit = new Regex(@",", options);

            MatchCollection matches = rxFind.Matches(description);
            if (matches.Count == 1)
            {
                string IDs = matches[0].Value.Replace("{", "").Replace("}", "");
                string[] ids = rxSplit.Split(IDs);
                foreach (string id in ids)
                {
                    int rc = Lib.UserInput.ToInt32(id);
                    if (!results.Contains(rc))
                    {
                        results.Add(rc);
                    }
                }
            }
            else
            {
                results.Add(0);
            }

            return results;
        }

        #endregion
    }
}