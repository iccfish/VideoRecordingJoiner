using FSLib.Extension;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoRecordingJoiner
{
	using System.Collections.Concurrent;

	internal class JoinWorker
	{
		public bool   IsGroupByMonth     { get; set; } = false;
		public string FileNameFormat     { get; set; } = string.Empty;
		public bool   DeleteAfterCombine { get; set; } = false;
		public bool   NeedEncodeAudio    { get; set; } = false;
		public string Target             { get; set; } = string.Empty;

		/// <summary>
		/// 获得或设置是否显示详细输出信息
		/// </summary>
		public bool Verbose { get; set; } = false;

		/// <summary>
		/// 获得或设置文件类型，默认为mkv
		/// </summary>
		public string FileType { get; set; } = "mkv";

		public string FfmpegCmd() => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

		private List<string> _pendingRemoveFileList = new();

		/// <summary>
		/// 尝试修复 moov_atom_not_found 错误文件，需要 untrunc 工具
		/// </summary>
		public bool TryFixMoovAtom { get; set; }

		/// <summary>
		/// 尝试忽略错误文件
		/// </summary>
		public bool IgnoreErrorFile { get; set; }

		DateTime? ParseDateTimeFromFileName(string fileNameWithoutExt)
		{
			// 小米室外摄像机CW500双摄版 录像文件的文件名以 00_ 或 10_ 开头，其它同
			if (!fileNameWithoutExt.IsNullOrEmpty() && Regex.IsMatch(fileNameWithoutExt, @"^\d{2}_"))
				fileNameWithoutExt = Regex.Replace(fileNameWithoutExt, @"^\d{2}_", "");

			var m = Regex.Match(fileNameWithoutExt, @"^\d+M\d+S_(\d+)$", RegexOptions.IgnoreCase);
			if (m.Success)
			{
				return DateTimeEx.FromUnixTicks(m.GetGroupValue(1).ToInt64());
			}
			//20240320192213_20240320193309.mp4
			m = Regex.Match(fileNameWithoutExt, @"^(\d{14})_\d{14}$", RegexOptions.IgnoreCase);
			if (m.Success)
			{
				var time = DateTime.ParseExact(m.GetGroupValue(1), "yyyyMMddHHmmss", null);
				return time;
			}

			return null;
		}

		// 获得目标文件名
		string GetTargetPathName(int key)
		{
			var year  = key >> 9;
			var month = key >> 5 & 0b1111;
			var day   = key      & 0b11111;

			return FileNameFormat.Replace("yyyy", year.ToString("d4")).Replace("MM", month.ToString("d2")).Replace("dd", day.ToString("d2")) + "." + FileType;
		}

		bool TestPermission(string path)
		{
			try
			{
				File.Create(path).Close();
				File.Delete(path);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		async Task<bool> CombineAsync(string[] src, string outFile, bool encodeAudio)
		{
			var tmpFile = Path.GetTempFileName();
			File.WriteAllLines(tmpFile, src.Select(s => $"file '{s}'").ToArray());

			if (!TestPermission(outFile))
			{
				Console.WriteLine("- 错误：创建文件权限被拒绝！");
				Environment.Exit(0);
			}

			var cmdLine = $"-hide_banner -safe 0 -f concat -i \"{tmpFile}\" -c:v copy {(encodeAudio && NeedEncodeAudio ? "-c:a aac" : "-c:a copy")} -f {(FileType == "mkv" ? "matroska" : FileType)} \"{outFile}\"";
			var psi = new ProcessStartInfo(FfmpegCmd(), cmdLine)
			{
				RedirectStandardError  = true,
				RedirectStandardOutput = true,
				UseShellExecute        = false,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding  = Encoding.UTF8,
				CreateNoWindow         = true,
				WindowStyle            = ProcessWindowStyle.Hidden
			};
			// 编码不支持
			var    cns       = false;
			var    mq        = new ConcurrentQueue<string>();
			var    sp        = 2;
			string errorFile = null;
			string errorMsg  = null;

			Task HandleStreamAsync(StreamReader sr)
			{
				string buffer;

				while (!(buffer = sr.ReadLine()!).IsNullOrEmpty())
				{
					Debug.WriteLine(buffer);
					if (buffer.Contains("codec not currently supported in container"))
					{
						cns = true;
					}

					mq.Enqueue(buffer);
				}
				Interlocked.Decrement(ref sp);

				return Task.CompletedTask;
			}

			var p = Process.Start(psi)!;
			await Task.WhenAll(
					Task.Factory.StartNew(() => HandleStreamAsync(p.StandardOutput)),
					Task.Factory.StartNew(() => HandleStreamAsync(p.StandardError)),
					Task.Factory.StartNew(() => p.WaitForExitAsync()),
					Task.Factory.StartNew(
						() => PrintMessagesAsync(
							ref sp,
							mq,
							(msg, file) =>
							{
								if (!p.HasExited) p.Kill();
								p.WaitForExit();
								Console.WriteLine($"警告：检测到错误 -> {file}: {msg}");
								errorMsg  = msg;
								errorFile = file;
							}))
				)
				.ConfigureAwait(false);
			File.Delete(tmpFile);

			if (!errorFile.IsNullOrEmpty() && IgnoreErrorFile)
			{
				Console.WriteLine($"警告：将跳过错误文件 {errorFile} 并尝试继续合并。");

				src = src.Where(s => string.Compare(s, errorFile, StringComparison.OrdinalIgnoreCase) != 0).ToArray();
				return await CombineAsync(src, outFile, encodeAudio).ConfigureAwait(false);
			}
			if (TryFixMoovAtom && errorMsg == "moov_atom_not_found")
			{
				Untrunc.OnAtomMoovNotFound(src, errorFile);
				Console.WriteLine($"提示：尝试自动修复错误文件 {errorFile} 。");

				var (dstFile, msg) = await Untrunc.TryUntruncVideoFileAsync(errorFile).ConfigureAwait(false);
				if (dstFile.IsNullOrEmpty())
				{
					Console.WriteLine($"错误：修复失败（{msg}）");
					return false;
				}
				// 替换文件
				src = src.Select(s => string.Compare(s, errorFile, StringComparison.OrdinalIgnoreCase) == 0 ? dstFile : s).ToArray()!;
				Console.WriteLine($"提示：使用自动修复文件 {dstFile} 替换原文件进行重新合并。");

				// 自动修复
				var result = await CombineAsync(src, outFile, encodeAudio).ConfigureAwait(false);
				File.Delete(dstFile);
				return result;
			}

			if (p.ExitCode == 0 && new FileInfo(outFile).Length > 0) return true;

			if (File.Exists(outFile))
				File.Delete(outFile);
			if (cns && !NeedEncodeAudio)
			{
				Console.WriteLine("警告：检测到编码错误，尝试强制转码音频！");
				NeedEncodeAudio = true;
				return await CombineAsync(src, outFile, encodeAudio).ConfigureAwait(false);
			}
			return false;
		}

		Task PrintMessagesAsync(ref int sp, ConcurrentQueue<string> q, Action<string, string> errorDetected)
		{
			var    notFirst  = false;
			string errMsg    = null;
			string errorFile = null;
			Match  m;

			while (sp > 0)
			{
				if (q.TryDequeue(out var line))
				{
					if (line.Contains("moov atom not found"))
						errMsg = "moov_atom_not_found";
					else if ((m = Regex.Match(line, @"Impossible\sto\sopen\s['""]([^'""]+)", RegexOptions.IgnoreCase)).Success)
						errorFile = m.GetGroupValue(1);

					if (!errorFile.IsNullOrEmpty())
					{
						if (errMsg.IsNullOrEmpty())
							errMsg = "FILE_OPEN_ERROR";

						errorDetected?.Invoke(errMsg, errorFile);
						break;
					}

					if (Verbose)
						Console.WriteLine(line);
					else
					{
						var status = TryPrintProgress(line, notFirst);
						if (status == 1)
							notFirst = true;
						else if (status == 2)
							break;
					}
				}
				else Thread.SpinWait(100);
			}
			Console.WriteLine();

			return Task.CompletedTask;
		}

		int TryPrintProgress(string line, bool notFirst)
		{
			if (line.IsNullOrEmpty() || !Regex.IsMatch(line, @"(size=|overhead:)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
				return 0;

			line = Regex.Replace(line, @"((?<=[:=])\s+|\[[^\]]+\]\s+)", ""); // 清除在:=后有可能多的对齐空格
			// 对行进行拆解
			var matches = Regex.Matches(line, @"([\w\s]+)[=:]([\d:\.\-/+e\w%]+)\s*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
			var dataMap = matches.ToDictionary(m => m.GetGroupValue(1), m => m.GetGroupValue(2));

			TimeSpan ToTimeSpan(string str)
			{
				var m = Regex.Match(str, @"$(\d+):(\d+):(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
				if (m.Success)
				{
					return new TimeSpan(0, m.GetGroupValue(1).ToInt32(), m.GetGroupValue(2).ToInt32(), m.GetGroupValue(3).ToInt32(), m.GetGroupValue(4).ToInt32());
				}

				return TimeSpan.Parse(str);
			}

			long ToSize(string  str) => (long)(Regex.Match(str, @"^[\d\.]+").GetGroupValue(0).ToDouble() * 1024);
			int  ToSpeed(string str) => Regex.Match(str, @"^[\de\.+]+").GetGroupValue(0).ToInt32();

			var sb       = new StringBuilder();
			var isResult = false;
			if (dataMap.ContainsKey("speed"))
			{
				if (dataMap.TryGetValue("frame", out var frame)) sb.Append($"帧数:{frame} ");

				if (dataMap.TryGetValue("fps", out var fps)) sb.Append($"处理速度:{fps}帧/秒({ToSpeed(dataMap["speed"])}倍速) ");
				else sb.Append($"处理速度:{ToSpeed(dataMap["speed"])}倍速 ");

				sb.Append($"视频时长:{ToTimeSpan(dataMap["time"]).ToFriendlyDisplayShort()} ");
				sb.Append($"码率:{(ToSize(dataMap["bitrate"])).ToSizeDescription(1)}/s");
			}
			else
			{
				sb.Append($"编码结果 => 视频大小:{ToSize(dataMap["video"]).ToSizeDescription(1)}, ");
				sb.Append($"音频大小:{(ToSize(dataMap["audio"])).ToSizeDescription(1)}");
				isResult = true;
			}
			if (notFirst)
				Console.Write("\u001b[2K\r");
			Console.Write(sb.ToString());

			return isResult ? 2 : 1;
		}

		public List<string> SourceFiles { get; } = new List<string>();

		public async Task JoinAsync()
		{
			if (FileNameFormat.IsNullOrEmpty())
				FileNameFormat = IsGroupByMonth ? "yyyy-MM" : $"yyyy-MM{Path.DirectorySeparatorChar}dd";

			// 定位所有文件和目录
			Console.WriteLine("正在搜索文件和目录...");

			var files = SourceFiles.SelectMany(
					s => Directory.Exists(s) ? Directory.GetFiles(s, "*.mp4", SearchOption.AllDirectories) : File.Exists(s) ? new[] { s } : new string[0] { })
				.ToArray();
			Console.WriteLine($"搜索到了 {files.Length} 个文件，正在预处理 ...");

			var tasks = new List<(DateTime dateTime, string path)>();

			foreach (var file in files)
			{
				var m = ParseDateTimeFromFileName(Path.GetFileNameWithoutExtension(file));
				if (m != null)
					tasks.Add((m.Value, file));
			}

			if (tasks.Count == 0)
			{
				Console.WriteLine("请通过参数指定要合并的文件或文件夹！");
				return;
			}
			if (Target.IsNullOrEmpty())
			{
				Target = Environment.CurrentDirectory;
				Console.WriteLine("未指定输出目录，自动设定为当前目录");
			}
			Target = Path.GetFullPath(Target);
			Console.WriteLine($"合并后的文件位置在：{Target}");
			if (DeleteAfterCombine) Console.WriteLine("合并后将会删除源文件");
			else Console.WriteLine("合并后将会重命名源文件");
			Console.WriteLine($"将要合并 {tasks.Count} 个视频");

			// 对任务进行分组
			var tgs = tasks.GroupBy(s => (s.dateTime.Year << 9) | (s.dateTime.Month << 5) | (IsGroupByMonth ? 0 : s.dateTime.Day)).Select(s => new { key = s.Key, list = s.OrderBy(x => x.dateTime).Select(x => x.path).ToArray() }).OrderBy(s => s.key).ToList();
			foreach (var tg in tgs)
			{
				var outName = GetTargetPathName(tg.key);
				var outFile = Path.Combine(Target, outName);
				var tmpFile = outFile + ".tmp";

				Console.WriteLine($"=> 初次合并，目标路径：{outFile}");
				Console.WriteLine($"=> 包含 {tg.list.Length} 视频：");
				for (int i = 0; i < tg.list.Length; i++)
				{
					Console.WriteLine($"  [{i + 1:00000}] {tg.list[i]}");
				}

				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
				}
				catch (Exception e)
				{
					Console.WriteLine($"{tg.key} => 创建文件夹失败：{e.Message}");
					continue;
				}

				var srcFullPath = tg.list.Select(Path.GetFullPath).ToArray();
				if (await CombineAsync(srcFullPath, tmpFile, true).ConfigureAwait(false))
				{
					if (TryFixMoovAtom)
						Untrunc.LogSucceedCombine(srcFullPath);

					Console.WriteLine("- 合并成功！");

					if (File.Exists(outFile))
					{
						Console.WriteLine("- 已存在原始合并文件，二次合并！");
						if (await CombineAsync(new[] { outFile, tmpFile }, tmpFile + ".swp", false).ConfigureAwait(false))
						{
							Console.WriteLine("- 二次合并完成！");
							File.Delete(outFile);
							File.Delete(tmpFile);
							File.Move(tmpFile + ".swp", outFile);
						}
						else
						{
							Console.WriteLine("- 二次合并失败！");
							continue;
						}
					}
					else
					{
						File.Move(tmpFile, outFile);
					}

					Console.WriteLine($"- 正在{(DeleteAfterCombine ? "删除" : "重命名")}源文件");
					foreach (var file in srcFullPath)
					{
						try
						{
							if (Untrunc.IsReferencedByUntrunc(file))
							{
								Console.WriteLine($"  - 自动修复参考文件：{file}，暂时保留");
								_pendingRemoveFileList.Add(file);
							}
							else if (DeleteAfterCombine)
							{
								File.Delete(file);
								Console.WriteLine($"  - 已删除：{file}");
							}
							else
							{
								Console.WriteLine($"  - 重命名：{file}");
								File.Move(file, file + ".old");
							}
						}
						catch (Exception e)
						{
							Console.WriteLine($"  - 无法操作：{file} -> {e.Message}");
						}
					}
					Console.WriteLine("- 操作完成！");
				}
				else
				{
					Console.WriteLine("- 合并失败！");
					if (File.Exists(tmpFile))
						File.Delete(tmpFile);
				}
				CleanUpPendingFile(false);
			}

			CleanUpPendingFile(true);
		}

		/// <summary>
		/// 清理待清理文件
		/// </summary>
		/// <param name="finalCleanup"></param>
		void CleanUpPendingFile(bool finalCleanup)
		{
			if (!_pendingRemoveFileList.Any())
				return;

			try
			{
				foreach (var file in _pendingRemoveFileList)
				{
					if (finalCleanup || !Untrunc.IsReferencedByUntrunc(file))
					{
						if (DeleteAfterCombine)
						{
							File.Delete(file);
							File.Delete($"提示：已删除暂存文件 {file}");
						}
						else
						{
							Console.WriteLine($"提示：已重命名暂存文件 {file}");
							File.Move(file, file + ".old");
						}
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine($"警告：处理暂存文件失败 -> {e.Message}");
			}
		}

		/// <summary>
		/// 校验FFMPEG是否安装
		/// </summary>
		/// <returns></returns>
		public async Task<bool> CheckFfmpegAsync()
		{
			var psi = new ProcessStartInfo(FfmpegCmd(), "-version")
			{
				WindowStyle            = ProcessWindowStyle.Hidden,
				RedirectStandardError  = true,
				RedirectStandardOutput = true,
				CreateNoWindow         = true,
				UseShellExecute        = false
			};
			try
			{
				var p = Process.Start(psi)!;
				await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
				await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
				await p.WaitForExitAsync().ConfigureAwait(false);

				return p.ExitCode == 0;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
