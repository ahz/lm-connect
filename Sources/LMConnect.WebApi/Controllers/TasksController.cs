﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Http;
using System.Xml.Linq;
using System.Xml.XPath;
using LMConnect.LISpMiner;
using LMConnect.WebApi.API;
using LMConnect.WebApi.API.Exceptions;
using LMConnect.WebApi.API.Requests.Task;
using LMConnect.WebApi.API.Responses.Task;
using log4net;

namespace LMConnect.WebApi.Controllers
{
	[Authorize]
	public class TasksController : ApiBaseController
	{
		#region Private Helpers

		private static readonly ILog log = LogManager.GetLogger(typeof(TasksController));
		private const string DefaultTemplate = "4ftMiner.Task.Template.PMML";
		private static readonly string InvalidChars = String.Format(@"[{0}]+", Regex.Escape(new string(Path.GetInvalidFileNameChars())));

		protected ILog Log
		{
			get { return log; }
		}

		protected const string XPathStatus = "/*/*/TaskSetting/Extension/TaskState";
		protected const string XPathNumberOfRules = "/*/*/@numberOfRules";
		protected const string XPathHypothesesCountMax = "/*/*/TaskSetting/Extension/HypothesesCountMax";

		private class TaskDefinition
		{
			public string DefaultTemplate { get; set; }

			public ITaskLauncher Launcher { get; set; }

			public string TaskName { get; set; }
		}

		protected static string RemoveInvalidXmlChars(string input)
		{
			return new string(input.Where(value =>
				(value >= 0x0020 && value <= 0xD7FF) ||
				(value >= 0xE000 && value <= 0xFFFD) ||
				value == 0x0009 ||
				value == 0x000A ||
				value == 0x000D).ToArray());
		}

		protected void GetInfo(string xmlPath, out string status, out int numberOfRules, out int hypothesesCountMax)
		{
			using (var reader = new StreamReader(xmlPath, System.Text.Encoding.UTF8))
			{
				var xml = RemoveInvalidXmlChars(reader.ReadToEnd());
				var document = XDocument.Parse(xml);

				var statusAttribute = ((IEnumerable<object>)document.XPathEvaluate(XPathStatus)).FirstOrDefault() as XElement;
				var numberOfRulesAttribute =
					((IEnumerable<object>)document.XPathEvaluate(XPathNumberOfRules)).FirstOrDefault() as XAttribute;
				var hypothesesCountMaxAttribute =
					((IEnumerable<object>)document.XPathEvaluate(XPathHypothesesCountMax)).FirstOrDefault() as XElement;

				if (statusAttribute == null)
				{
					throw new InvalidTaskResultXml("TaskState cannot be resolved.", xmlPath, XPathStatus);
				}

				status = statusAttribute.Value;

				if (numberOfRulesAttribute == null || !Int32.TryParse(numberOfRulesAttribute.Value, out numberOfRules))
				{
					throw new InvalidTaskResultXml("NumberOfRulesAttribute cannot be resolved.", xmlPath, XPathNumberOfRules);
				}

				if (hypothesesCountMaxAttribute == null ||
					!Int32.TryParse(hypothesesCountMaxAttribute.Value, out hypothesesCountMax))
				{
					throw new InvalidTaskResultXml("HypothesesCountMax cannot be resolved.", xmlPath, XPathHypothesesCountMax);
				}
			}
		}

		protected ITaskLauncher GetTaskLauncher(string taskType)
		{
			switch (taskType)
			{
				case "proc":
					return this.LISpMiner.LMProcPooler;
				case "grid":
					return this.LISpMiner.LMGridPooler;
				case "task":
				default:
					return this.LISpMiner.LMTaskPooler;
			}
		}

		public string GetTaskFileName(string taskName)
		{
			return Regex.Replace(taskName, InvalidChars, "_");
		}

		private TaskResponse RunTask(TaskDefinition definition, TaskRequest request, string template, string alias)
		{
			var exporter = this.LISpMiner.Exporter;
			var importer = this.LISpMiner.Importer;

			try
			{
				var response = new TaskResponse();

				if (this.LISpMiner != null && request.Task != null)
				{
					var status = "Not generated";
					var numberOfRules = 0;
					var hypothesesCountMax = Int32.MaxValue;

					exporter.Output = string.Format("{0}/results_{1}_{2:yyyyMMdd-Hmmss}.xml", this.DataFolder,
													request.TaskFileName, DateTime.Now);

					exporter.Template = string.Format(@"{0}\Sewebar\Template\{1}", exporter.LMExecutablesPath, template);

					exporter.TaskName = request.TaskName;
					exporter.NoEscapeSeqUnicode = true;

					try
					{
						// try to export results
						exporter.Execute();

						if (!System.IO.File.Exists(exporter.Output))
						{
							throw new LISpMinerException("Task possibly does not exist but no appLog generated");
						}

						this.GetInfo(exporter.Output, out status, out numberOfRules, out hypothesesCountMax);
					}
					catch (LISpMinerException ex)
					{
						// task was never imported - does not exists. Therefore we need to import at first.
						Log.Debug(ex);

						// import task
						var taskPath = string.Format("{0}/task_{1}_{2:yyyyMMdd-Hmmss}.xml",
												   this.DataFolder,
												   request.TaskFileName,
												   DateTime.Now); ;

						importer.Input = request.WriteTask(taskPath);
						importer.NoCheckPrimaryKeyUnique = true;

						if (!string.IsNullOrEmpty(alias))
						{
							importer.Alias = String.Format(@"{0}\Sewebar\Template\{1}", importer.LMExecutablesPath, alias);
						}

						importer.Execute();
					}

					switch (status)
					{
						// * Not Generated (po zadání úlohy nebo změně v zadání)
						case "Not generated":
						// * Interrupted (přerušena -- buď kvůli time-outu nebo max počtu hypotéz)
						case "Interrupted":
							// run task - generate results
							if (definition.Launcher.Status == ExecutableStatus.Ready)
							{
								var taskLauncher = definition.Launcher;
								taskLauncher.TaskName = request.TaskName;
								taskLauncher.ShutdownDelaySec = 0;

								taskLauncher.Execute();
							}
							else
							{
								Log.Debug("Waiting for result generation");
							}

							// run export once again to refresh results and status
							if (status != "Interrupted" && exporter.Status == ExecutableStatus.Ready)
							{
								exporter.Execute();
							}
							break;
						// * Running (běží generování)
						case "Running":
						// * Waiting (čeká na spuštění -- pro TaskPooler, zatím neimplementováno)
						case "Waiting":
							definition.Launcher.ShutdownDelaySec = 10;
							break;
						// * Solved (úspěšně dokončena)
						case "Solved":
						case "Finnished":
						default:
							break;
					}

					response.OutputFilePath = exporter.Output;

					if (!System.IO.File.Exists(response.OutputFilePath))
					{
						throw new Exception("Results generation did not succeed.");
					}
				}
				else
				{
					throw new Exception("No LISpMiner instance or task defined");
				}

				return response;
			}
			finally
			{
				// clean up
				exporter.Output = String.Empty;
				exporter.Template = String.Empty;
				exporter.TaskName = String.Empty;

				importer.Input = String.Empty;
				importer.Alias = String.Empty;
				importer.NoCheckPrimaryKeyUnique = false;
			}
		}

		private TaskResponse ExportTask(string taskName, string template, string alias)
		{
			LMSwbExporter exporter = this.LISpMiner.Exporter;

			try
			{
				var response = new TaskResponse();

				if (this.LISpMiner != null && taskName != null)
				{
					exporter.Output = string.Format("{0}/results_{1}_{2:yyyyMMdd-Hmmss}.xml",
						this.DataFolder,
						GetTaskFileName(taskName),
						DateTime.Now);

					exporter.Template = string.Format(@"{0}\Sewebar\Template\{1}",
						exporter.LMExecutablesPath,
						template);

					exporter.TaskName = taskName;
					exporter.NoEscapeSeqUnicode = true;

					// try to export results
					exporter.Execute();

					if (!System.IO.File.Exists(exporter.Output))
					{
						throw new LISpMinerException("Results generation did not succeed. Task possibly does not exist but no appLog generated.");
					}

					response.OutputFilePath = exporter.Output;
				}
				else
				{
					throw new Exception("No LISpMiner instance or task defined.");
				}

				return response;
			}
			finally
			{
				// clean up
				exporter.Output = String.Empty;
				exporter.Template = String.Empty;
				exporter.TaskName = String.Empty;
			}
		}

		private Response CancelTask(TaskDefinition definition)
		{
			var pooler = definition.Launcher;

			try
			{
				// cancel task
				pooler.TaskCancel = true;
				pooler.TaskName = definition.TaskName;
				pooler.Execute();

				return new Response
					{
						Message = String.Format("Task {0} has been canceled.", definition.TaskName)
					};
			}
			finally
			{
				// clean up
				pooler.CancelAll = false;
				pooler.TaskCancel = false;
				pooler.TaskName = String.Empty;
			}
		}

		private Response CancelAll(TaskDefinition definition)
		{
			ITaskLauncher pooler = definition != null ? definition.Launcher : null;

			try
			{
				if (pooler != null)
				{
					pooler.CancelAll = true;
					pooler.Execute();
				}
				else
				{
					// cancel all
					this.LISpMiner.LMTaskPooler.CancelAll = true;
					this.LISpMiner.LMTaskPooler.Execute();

					this.LISpMiner.LMProcPooler.CancelAll = true;
					this.LISpMiner.LMProcPooler.Execute();

					this.LISpMiner.LMGridPooler.CancelAll = true;
					this.LISpMiner.LMGridPooler.Execute();
				}

				return new Response
					{
						Message = "All tasks has been canceled."
					};
			}
			finally
			{
				if (pooler != null)
				{
					pooler.CancelAll = false;
					pooler.TaskCancel = false;
					pooler.TaskName = String.Empty;
				}
				else
				{
					// clean up
					this.LISpMiner.LMTaskPooler.CancelAll = false;
					this.LISpMiner.LMTaskPooler.TaskCancel = false;
					this.LISpMiner.LMTaskPooler.TaskName = String.Empty;

					this.LISpMiner.LMProcPooler.CancelAll = false;
					this.LISpMiner.LMProcPooler.TaskCancel = false;
					this.LISpMiner.LMProcPooler.TaskName = String.Empty;

					this.LISpMiner.LMGridPooler.CancelAll = false;
					this.LISpMiner.LMGridPooler.TaskCancel = false;
					this.LISpMiner.LMGridPooler.TaskName = String.Empty;
				}
			}
		}

		private Response GetAllTasks()
		{
			// TODO: List of all Tasks for given miner
			return new Response
				{
					Message = "TODO: List of all Tasks for given miner"
				};
		}

		private void CheckMinerOwnerShip()
		{
			var user = this.GetLMConnectUser();
			var miner = this.Repository.Query<LMConnect.Key.Miner>()
				.FirstOrDefault(m => m.MinerId == this.LISpMiner.Id);

			if ((miner != null && user.Username != miner.Owner.Username) && !this.User.IsInRole("admin"))
			{
				this.ThrowHttpReponseException("Authorized user is not allowed to use this miner.", HttpStatusCode.Forbidden);
			}
		}

		#endregion

		[Filters.NHibernateTransaction]
		public Response Get(string taskName = null, string template = null, string alias = null)
		{
			CheckMinerOwnerShip();

			// when exporting we dont need to know what was task type
			var name = taskName ?? this.ControllerContext.RouteData.Values["taskType"] as string;

			if (string.IsNullOrEmpty(name))
			{
				return this.GetAllTasks();
			}
			else
			{
				template = string.IsNullOrEmpty(template) ? DefaultTemplate : template;

				return this.ExportTask(name, template, alias);
			}
		}

		[Filters.NHibernateTransaction]
		public TaskResponse Post(TaskRequest request, string template = null, string alias = null, string taskType = "task")
		{
			CheckMinerOwnerShip();

			var definition = new TaskDefinition
				{
					DefaultTemplate = DefaultTemplate,
					Launcher = this.GetTaskLauncher(taskType)
				};

			template = string.IsNullOrEmpty(template) ? DefaultTemplate : template;

			return this.RunTask(definition, request, template, alias);
		}

		[Filters.NHibernateTransaction]
		public Response Put(TaskUpdateRequest request, string taskType = null, string taskName = null)
		{
			CheckMinerOwnerShip();

			var hasTaskType = !string.IsNullOrEmpty(taskType);
			var hasTaskName = !string.IsNullOrEmpty(taskName);

			if (!request.IsCancelation)
			{
				throw new Exception("Unsupported task update request.");
			}

			var definition = new TaskDefinition
				{
					DefaultTemplate = DefaultTemplate
				};

			if (!hasTaskType && !hasTaskName)
			{
				return this.CancelAll(null);
			}
			else if (hasTaskType && !hasTaskName)
			{
				definition.Launcher = this.GetTaskLauncher(taskType);

				return this.CancelAll(definition);
			}
			else if (hasTaskType && hasTaskName)
			{
				definition.Launcher = this.GetTaskLauncher(taskType);
				definition.TaskName = taskName;

				return this.CancelTask(definition);
			}
			else // if (!hasTaskType && hasTaskName)
			{
				throw new Exception();
			}
		}
	}
}
