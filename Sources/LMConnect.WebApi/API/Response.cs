﻿using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LMConnect.WebApi.API
{
	[DataContract]
	public class Response
	{
		[DataMember(Name = "message")]
		public string Message { get; set; }

		[DataMember(Name = "status")]
		public Status Status { get; set; }

		public Response()
		{
			this.Status = Status.Success;
		}

		public Response(string message)
			: this()
		{
			this.Message = message;
		}

		protected virtual XDocument XDocument
		{
			get
			{
				return new XDocument(
					new XDeclaration("1.0", "utf-8", "yes"),
					new XElement("response",
					             new XAttribute("status", this.Status.ToString().ToLower()),
					             GetBody()
						)
					);
			}
		}

		protected virtual XElement GetBody()
		{
			return new XElement("message", this.Message);
		}

		public virtual void WriteToStream(Stream m)
		{
			using (var sw = new XmlTextWriter(m, Encoding.UTF8))
			{
				this.XDocument.Save(sw);
			}
		}
	}
}