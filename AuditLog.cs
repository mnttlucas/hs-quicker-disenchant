using System;
using System.IO;

namespace HsQuickerDisenchant
{
	/// <summary>
	/// Text file recording what a run did. One file per game session, created at the first write (so starting the
	/// game without disenchanting anything creates nothing). Only the most recent files are kept. Never throws.
	/// </summary>
	public sealed class AuditLog
	{
		private const string FilePrefix = "HsQuickerDisenchant-audit-";
		private const string FileExtension = ".log";
		private const int MaxFiles = 5;

		private readonly string _directory;
		private string _filePath;
		private bool _warned;

		public AuditLog(string directory)
		{
			_directory = directory;
		}

		/// <summary>The current session's file, or the folder when nothing was written yet.</summary>
		public string FilePath
		{
			get
			{
				return _filePath ?? _directory;
			}
		}

		public void Write(string eventName, string detail)
		{
			try
			{
				if (_filePath == null)
					_filePath = CreateSessionFile();

				string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + eventName + " | " + detail;
				File.AppendAllText(_filePath, line + Environment.NewLine);
			}
			catch (Exception exception)
			{
				if (!_warned)
				{
					_warned = true;
					Plugin.Log.LogWarning("Could not write the audit log in '" + _directory + "': " + exception.Message);
				}
			}
		}

		private string CreateSessionFile()
		{
			DeleteOldFiles();
			return Path.Combine(_directory, FilePrefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + FileExtension);
		}

		/// <summary>Keeps the newest MaxFiles - 1 files, so that with the new one there are MaxFiles.</summary>
		private void DeleteOldFiles()
		{
			try
			{
				string[] existing = Directory.GetFiles(_directory, FilePrefix + "*" + FileExtension);
				Array.Sort(existing, StringComparer.Ordinal);
				for (int i = 0; i < existing.Length - (MaxFiles - 1); i++)
					File.Delete(existing[i]);
			}
			catch (Exception exception)
			{
				Plugin.Log.LogWarning("Could not clean up old audit logs: " + exception.Message);
			}
		}
	}
}