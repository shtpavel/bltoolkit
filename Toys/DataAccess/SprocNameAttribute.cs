using System;

namespace Rsdn.Framework.DataAccess
{
	[AttributeUsage(AttributeTargets.Method)]
	public class SprocNameAttribute : Attribute
	{
		public SprocNameAttribute(string name)
		{
			_name = name;
		}

		private string _name;
		public  string  Name
		{
			get { return _name; }
		}
	}
}
