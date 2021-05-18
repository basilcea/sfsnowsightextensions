using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;

namespace Snowflake.Powershell
{
    [Cmdlet
        (VerbsCommon.New,
        "SFDashboard",
        DefaultParameterSetName="DashboardFile",
        SupportsPaging=false,
        SupportsShouldProcess=false)]
    [OutputType(typeof(String))]
    public class NewDashboardCommand : PSCmdlet
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static Logger loggerConsole = LogManager.GetLogger("Snowflake.Powershell.Console");

        Stopwatch stopWatch = new Stopwatch();

        [Parameter(
            Mandatory = true,
            Position = 0,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Application user context from authentication process")]
        public AppUserContext AuthContext { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "Dashboard object of Dashboard to create or update",
            ParameterSetName = "DashboardObject")]
        public Dashboard Dashboard { get; set; }

        [Parameter(
            Mandatory = true,
            Position = 1,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "File representation of Dashboard to create or update",
            ParameterSetName = "DashboardFile")]
        public string DashboardFile { get; set; }

        [Parameter(
            Mandatory = false,
            Position = 2,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            HelpMessage = "What to do when the Dashboard already exists")]
        [ValidateSet ("CreateNew", "Skip")]
        public string ActionIfExists { get; set; } = "Skip";

        protected override void BeginProcessing()
        {
            stopWatch.Start();

            NLogHelper.ConfigureNLog();

            logger = LogManager.GetCurrentClassLogger();
            loggerConsole = LogManager.GetLogger("Snowflake.Powershell.Console");

            logger.Trace("BEGIN {0}", this.GetType().Name);
            WriteVerbose(String.Format("BEGIN {0}", this.GetType().Name));
        }

        protected override void EndProcessing()
        {
            stopWatch.Stop();

            logger.Trace("END {0} execution took {1:c} ({2} ms)", this.GetType().Name, stopWatch.Elapsed, stopWatch.ElapsedMilliseconds);
            loggerConsole.Trace("Execution took {0:c} ({1} ms)", stopWatch.Elapsed, stopWatch.ElapsedMilliseconds);            
            WriteVerbose(String.Format("END {0}, execution took {1:c} ({2} ms)", this.GetType().Name, stopWatch.Elapsed, stopWatch.ElapsedMilliseconds));
            
            LogManager.Flush();
        }

        protected override void ProcessRecord()
        {
            try
            {
                logger.Info("ParameterSetName={0}", this.ParameterSetName);
                switch (this.ParameterSetName)
                {
                    case "DashboardObject":
                        break;

                    case "DashboardFile":
                        if (File.Exists(this.DashboardFile) == false)
                        {
                            throw new FileNotFoundException(String.Format("No Dashboard file found at {0}", this.DashboardFile));
                        }

                        this.Dashboard = JsonConvert.DeserializeObject<Dashboard>(FileIOHelper.ReadFileFromPath(this.DashboardFile));
                        if (this.Dashboard == null)
                        {
                            throw new ArgumentNullException(String.Format("Unable to convert file found at {0} to Dashboard", this.DashboardFile));
                        }

                        break;

                    default:
                        throw new ArgumentException(String.Format("Unknown parameter set {0}", this.ParameterSetName));
                }

                // Get all Dashboards already present
                string dashboardsApiResult = SnowflakeDriver.GetDashboards(this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.OrganizationID, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight);
                if (dashboardsApiResult.Length == 0)
                {
                    throw new ItemNotFoundException("Invalid response from listing dashboard entities");
                }

                Dashboard targetDashboardToReplace = null;

                // See if Dashboard we want to create already exists
                // First, try to find it by the Dashboard ID
                // Would probably only work for the Dashboards going from same deployment and user back into the same deployment
                JObject dashboardsPayloadObject = JObject.Parse(dashboardsApiResult);

                JArray entitiesArray = new JArray();
                if (JSONHelper.isTokenPropertyNull(dashboardsPayloadObject, "entities") == false)
                {
                    entitiesArray = (JArray)dashboardsPayloadObject["entities"];
                }
                logger.Info("Number of Entities={0}", entitiesArray.Count);
                
                foreach (JObject entityObject in entitiesArray)
                {
                    // Only deal with "query" objects, which are Dashboards
                    if (JSONHelper.getStringValueFromJToken(entityObject, "entityType") != "folder") continue;

                    Dashboard potentialTargetDashboard = new Dashboard(entityObject, dashboardsPayloadObject, this.AuthContext);

                    if (this.Dashboard.DashboardID == potentialTargetDashboard.DashboardID) 
                    {
                        targetDashboardToReplace = potentialTargetDashboard;

                        logger.Info("Found Match by ID: {0}=={1}", this.Dashboard, targetDashboardToReplace);
                        break;
                    }
                }

                // If didn't find it by the entity ID.
                // Second, try to find it by the Dashboard name
                if (targetDashboardToReplace == null)
                {
                    foreach (JObject entityObject in entitiesArray)
                    {
                        // Only deal with "query" objects, which are Dashboards
                        if (JSONHelper.getStringValueFromJToken(entityObject, "entityType") != "folder") continue;

                        Dashboard potentialTargetDashboard = new Dashboard(entityObject, dashboardsPayloadObject, this.AuthContext);

                        if (this.Dashboard.DashboardName == potentialTargetDashboard.DashboardName)
                        {
                            // Found first matching Dashboard with the same name and folder
                            targetDashboardToReplace = potentialTargetDashboard;

                            logger.Info("Found Match by Name: {0}=={1}", this.Dashboard, targetDashboardToReplace);
                            break;
                        }
                    }
                }

                // At this point, we either have the Dashboard to update, or the Dashboard we're trying to import is new
                if (targetDashboardToReplace != null)
                {
                    // Updating existing Dashboard
                    switch (this.ActionIfExists)
                    {
                        case "Overwrite":
                            logger.Info("Found {0} to overwrite and ActionIfExists=1{0}, will overwrite", targetDashboardToReplace, this.ActionIfExists);
                            loggerConsole.Info("Existing Dashboard '{0}' will be overwritten because ActionIfExists is '{1}'", targetDashboardToReplace.DashboardName, this.ActionIfExists);
                            
                            break;
                        
                        case "CreateNew":
                            logger.Info("Found {0} to overwrite but ActionIfExists={1}, will instead create new", targetDashboardToReplace, this.ActionIfExists);
                            loggerConsole.Info("Existing Dashboard '{0}' will be ignored and new Dashboard will be created because ActionIfExists is '{1}'", targetDashboardToReplace.DashboardName, this.ActionIfExists);

                            targetDashboardToReplace = null;
                        
                            break;

                        case "Skip":
                            logger.Info("Found {0} to overwrite but ActionIfExists={1}, will skip", targetDashboardToReplace, this.ActionIfExists);
                            loggerConsole.Info("Existing Dashboard '{0}' will be ignored and nothing will be done because ActionIfExists is '{1}'", targetDashboardToReplace.DashboardName, this.ActionIfExists);
                        
                            return;

                        default:
                            throw new ArgumentException(String.Format("Unknown ActionIfExists parameter {0}", this.ActionIfExists));
                    }
                }
                else
                {
                    logger.Info("No match for {0}, new one will be created", this.Dashboard);
                    loggerConsole.Info("Creating new Dashboard '{0}'", this.Dashboard.DashboardName);
}

                Dashboard createdOrUpdatedDashboard = null;

                // Now actually make modifications
                if (targetDashboardToReplace != null)
                {
                    // Updating existing Dashboard
                    throw new NotImplementedException("Replacement of the Dashboard is not yet implemented");
                }
                else
                {
                    // Creating new Dashboard
                    string createDashboardApiResult = SnowflakeDriver.CreateDashboard(
                        this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.OrganizationID, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight, 
                        this.Dashboard.DashboardName, this.Dashboard.Role, this.Dashboard.Warehouse);

                    if (createDashboardApiResult.Length == 0)
                    {
                        throw new ItemNotFoundException("Invalid response from creating new Dashboard");
                    }

                    JObject createDashboardPayloadObject = JObject.Parse(createDashboardApiResult);
                    string newDashboardID = JSONHelper.getStringValueFromJToken(createDashboardPayloadObject, "createdFolderId");
                    logger.Info("New DashboardID={0}", newDashboardID);

                    Dictionary<string, Worksheet> oldToNewWorksheetsDictionary = new Dictionary<string, Worksheet>();

                    // Create new Worksheets and Charts
                    foreach (Worksheet worksheetToCreate in this.Dashboard.Worksheets)
                    {
                        logger.Info("Creating new Worksheet for {0}", worksheetToCreate);
                        loggerConsole.Trace("Creating new Worksheet for {0} ({1})", worksheetToCreate.WorksheetName, worksheetToCreate.WorksheetID);

                        // Creating new worksheet
                        string createWorksheetApiResult = SnowflakeDriver.CreateWorksheet(
                            this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.OrganizationID, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight, 
                            worksheetToCreate.WorksheetName, newDashboardID);

                        if (createWorksheetApiResult.Length == 0)
                        {
                            throw new ItemNotFoundException("Invalid response from creating new worksheet");
                        }

                        JObject createWorksheetPayloadObject = JObject.Parse(createWorksheetApiResult);
                        string newWorksheetID = JSONHelper.getStringValueFromJToken(createWorksheetPayloadObject, "pid");
                        logger.Info("Original WorksheetID={0} -> New WorksheetID={1}", worksheetToCreate.WorksheetID, newWorksheetID);

                        string updateWorksheetApiResult = SnowflakeDriver.UpdateWorksheet(
                            this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight, 
                            newWorksheetID, worksheetToCreate.Query, worksheetToCreate.Role, worksheetToCreate.Warehouse, worksheetToCreate.Database, worksheetToCreate.Schema);

                        if (updateWorksheetApiResult.Length == 0)
                        {
                            throw new ItemNotFoundException("Invalid response from updating existing worksheet");
                        }

                        JObject updateWorksheetPayloadObject = JObject.Parse(updateWorksheetApiResult);

                        Worksheet worksheetCreated = new Worksheet(newWorksheetID, updateWorksheetPayloadObject, this.AuthContext);

                        logger.Info(worksheetCreated);
                        loggerConsole.Trace("Created new Worksheet {0} ({1})", worksheetCreated.WorksheetName, worksheetCreated.WorksheetID);

                        oldToNewWorksheetsDictionary.Add(worksheetToCreate.WorksheetID, worksheetCreated);

                        // Create charts
                        if (worksheetToCreate.Charts.Count > 0)
                        {
                            // Always create chart with maximum version number
                            Chart chartToCreate = worksheetToCreate.Charts.OrderBy(c => c.Version).Last();

                            logger.Info("Creating new Chart {0}", chartToCreate);
                            loggerConsole.Trace("Creating new Chart {0} ({1}) in {2} ({3})", chartToCreate.ChartName, chartToCreate.ChartID, worksheetCreated.WorksheetName, worksheetCreated.WorksheetID);

                            // Creating new worksheet
                            string createChartApiResult = SnowflakeDriver.CreateChartFromWorksheet(
                                this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight,
                                worksheetCreated.WorksheetID, chartToCreate.Configuration.ToString(Newtonsoft.Json.Formatting.None));
                        }
                    }

                    // Populate dashboard with widgets (Tables and Charts)
                    if (JSONHelper.isTokenPropertyNull(this.Dashboard.Contents, "rows") == false)
                    {
                        JArray rowsArray = (JArray)this.Dashboard.Contents["rows"];
                        int rowIndex = 0;                        
                        foreach (JObject rowObject in rowsArray)
                        {
                            int rowHeight = JSONHelper.getIntValueFromJToken(rowObject, "height");

                            if (JSONHelper.isTokenPropertyNull(rowObject, "cells") == false)
                            {
                                int cellIndex = 0;
                                JArray cellsArray = (JArray)rowObject["cells"];
                                foreach (JObject cellObject in cellsArray)
                                {
                                    // Only deal with "query" objects, which are worksheets
                                    if (JSONHelper.getStringValueFromJToken(cellObject, "type") != "query") continue;

                                    string originalWorksheetID = JSONHelper.getStringValueFromJToken(cellObject, "pid");

                                    Worksheet worksheetCreated = null;
                                    if (oldToNewWorksheetsDictionary.TryGetValue(originalWorksheetID, out worksheetCreated) == true)
                                    {
                                        // This is either "table" or "chart". 
                                        string displayMode = JSONHelper.getStringValueFromJToken(cellObject, "displayMode");

                                        logger.Info("Inserting {0} into cell ({1}, {2}) from Worksheet {3}", displayMode, rowIndex, cellIndex, worksheetCreated);
                                        loggerConsole.Trace("Inserting {0} into cell ({1}, {2}) from worksheet Worksheet {3} ({4})", displayMode, rowIndex, cellIndex, worksheetCreated.WorksheetName, worksheetCreated.WorksheetID);
                                        
                                        if (cellIndex == 0)
                                        {
                                            // Insert new row into the first cell
                                            string newRowApiResult = SnowflakeDriver.UpdateDashboardNewRowWithWorksheet(
                                                this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight,
                                                newDashboardID, worksheetCreated.WorksheetID, displayMode, rowIndex, rowHeight);
                                        }
                                        else
                                        {
                                            // Insert new cell into existing row
                                            string newCEllApiResult = SnowflakeDriver.UpdateDashboardInsertNewCellWithWorksheet(
                                                this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight,
                                                newDashboardID, worksheetCreated.WorksheetID, displayMode, rowIndex, rowHeight, cellIndex);
                                        }
                                    }

                                    cellIndex++;
                                }
                            }
                            rowIndex++;
                        }
                    }

                    // Execute worksheet
                    foreach (Worksheet worksheetToCreate in this.Dashboard.Worksheets)
                    {
                        Worksheet worksheetCreated = null;
                        if (oldToNewWorksheetsDictionary.TryGetValue(worksheetToCreate.WorksheetID, out worksheetCreated) == true)
                        {
                            logger.Info("Running new Worksheet {0}", worksheetCreated);
                            loggerConsole.Trace("Running new Worksheet {0} ({1})", worksheetCreated.WorksheetName, worksheetCreated.WorksheetID);

                            string executeWorksheetApiResult = SnowflakeDriver.ExecuteWorksheet(
                                this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight, 
                                worksheetCreated.WorksheetID, worksheetCreated.Query, worksheetCreated.Role, worksheetCreated.Warehouse, worksheetCreated.Database, worksheetCreated.Schema);
                        }
                    }

                    // Get final dashboard
                    string getDashboardApiResult = SnowflakeDriver.GetDashboard(
                        this.AuthContext.AppServerUrl, this.AuthContext.AccountUrl, this.AuthContext.UserName, this.AuthContext.AuthTokenSnowsight, 
                        newDashboardID);

                    if (getDashboardApiResult.Length == 0)
                    {
                        throw new ItemNotFoundException("Invalid response from getting existing Dashboard");
                    }

                    JObject getDashboardPayloadObject = JObject.Parse(getDashboardApiResult);

                    createdOrUpdatedDashboard = new Dashboard(newDashboardID, getDashboardPayloadObject, this.AuthContext);

                    logger.Info("Returning new Dashboard {0}", createdOrUpdatedDashboard);
                }

                loggerConsole.Info("Returning Dashboard '{0} ({1})'", createdOrUpdatedDashboard.DashboardName, createdOrUpdatedDashboard.DashboardID);

                WriteObject(createdOrUpdatedDashboard);
            }
            catch (Exception ex)
            {
                logger.Error("{0} threw {1} ({2})", this.GetType().Name, ex.Message, ex.Source);
                logger.Error(ex);

                if (ex is ItemNotFoundException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
                }
                else if (ex is FileNotFoundException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
                }
                else if (ex is ArgumentNullException)
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.ObjectNotFound, null));
                }
                else
                {
                    this.ThrowTerminatingError(new ErrorRecord(ex, "errorid", ErrorCategory.OperationStopped, null));
                }
            }
            finally
            {
                LogManager.Flush();
            }
        }
    }
}