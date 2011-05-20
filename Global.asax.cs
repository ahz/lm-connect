﻿using System;
using System.IO;
using LMWrapper.LISpMiner;
using LMWrapper.ODBC;

namespace SewebarWeb
{
	public class Global : System.Web.HttpApplication
	{
		private static LMWrapper.Environment _env;

		public static LMWrapper.Environment Environment
		{
			get
			{
				if(_env == null)
				{
					_env = new LMWrapper.Environment
					{
						DataPath = AppDomain.CurrentDomain.GetData("DataDirectory").ToString(),
						LMPoolPath = String.Format(@"{0}\Libs\", System.AppDomain.CurrentDomain.BaseDirectory),
						LMPath = String.Format(@"{0}\Libs\{1}", System.AppDomain.CurrentDomain.BaseDirectory, "LISp Miner"),
					};
				}

				return _env;
			}
		}

		protected void Application_Start(object sender, EventArgs e)
		{
			if (!ODBCManagerRegistry.DSNExists("Barbora"))
			{
				ODBCManagerRegistry.CreateDSN("Barbora",
											  "",
											  "Microsoft Access Driver (*.mdb)",
											  String.Format(@"{0}\Barbora.mdb", AppDomain.CurrentDomain.GetData("DataDirectory")));
			}
		}

		protected void Session_Start(object sender, EventArgs e)
		{
			if(Session["LM"] == null)
				Session["LM"] = new LISpMiner(Environment, Session.SessionID);
		}

		protected void Application_BeginRequest(object sender, EventArgs e)
		{

		}

		protected void Application_AuthenticateRequest(object sender, EventArgs e)
		{

		}

		protected void Application_Error(object sender, EventArgs e)
		{

		}

		protected void Session_End(object sender, EventArgs e)
		{
			var LM = Session["LM"] as IDisposable;
			
			if(LM != null)
			{
				LM.Dispose();
			}
		}

		protected void Application_End(object sender, EventArgs e)
		{
			
		}
	}
}