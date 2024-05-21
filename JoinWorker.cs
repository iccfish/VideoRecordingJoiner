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
		public bool   IsWin              { get; }      = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
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

		public string FfmpegCmd() => IsWin ? "ffmpeg.exe" : "ffmpeg";

		DateTime? ParseDateTimeFromFileName(string fileNameWithoutExt)
		{
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

		bool DeleteFiles(string[] target)
		{
			Console.WriteLine("- 正在删除源文件");
			try
			{
				foreach (var file in target)
				{
					Console.WriteLine($"  - {file}");
					File.Delete(file);
				}
				Console.WriteLine("- 删除源文件完成！");
			}
			catch (Exception e)
			{
				Console.WriteLine($"  - 删除失败：{e}");
				return false;
			}

			return true;
		}

		bool RenameFiles(string[] target)
		{
			Console.WriteLine("- 正在重命名源文件");
			try
			{
				foreach (var file in target)
				{
					Console.WriteLine($"  - {file}");
					File.Move(file, file + ".old");
				}
				Console.WriteLine("- 重命名源文件完成！");
			}
			catch (Exception e)
			{
				Console.WriteLine($"  - 重命名失败：{e}");
				return false;
			}

			return true;
		}

		async Task<bool> CombineAsync(string[] src, string outFile, bool encodeAudio)
		{
			var tmpFile = Path.GetTempFileName();
			File.WriteAllLines(tmpFile, src.Select(s => $"file '{Path.GetFullPath(s)}'").ToArray());

			var cmdLine = $"-safe 0 -f concat -i \"{tmpFile}\" -c:v copy {(encodeAudio && NeedEncodeAudio ? "-c:a aac" : "-c:a copy")} -f {(FileType == "mkv" ? "matroska" : FileType)} \"{outFile}\"";
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
			var cns = false;
			var mq  = new ConcurrentQueue<string>();
			var sp  = 2;

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

			var p  = Process.Start(psi)!;
			await Task.WhenAll(
					Task.Factory.StartNew(() => HandleStreamAsync(p.StandardOutput)),
					Task.Factory.StartNew(() => HandleStreamAsync(p.StandardError)),
					Task.Factory.StartNew(() => p.WaitForExit()),
					Task.Factory.StartNew(() => PrintMessagesAsync(ref sp, mq))
				)
				.ConfigureAwait(false);
			File.Delete(tmpFile);

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

		Task PrintMessagesAsync(ref int sp, ConcurrentQueue<string> q)
		{
			var notFirst = false;

			while (sp > 0)
			{
				if (q.TryDequeue(out var line))
				{
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
			if (notFirst)
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

			TimeSpan ToTimeSpan(string str) => TimeSpan.Parse(str);
			long     ToSize(string     str) => (long)(Regex.Match(str, @"^[\d\.]+").GetGroupValue(0).ToDouble() * 1024);
			int      ToSpeed(string    str) => Regex.Match(str, @"^[\de\.+]+").GetGroupValue(0).ToInt32();

			var sb       = new StringBuilder();
			var isResult = false;
			if (dataMap.ContainsKey("speed"))
			{
				if (dataMap.TryGetValue("frame", out var frame)) sb.Append($"帧数={frame} ");

				if (dataMap.TryGetValue("fps", out var fps)) sb.Append($"处理速度={fps}帧/秒({ToSpeed(dataMap["speed"])}倍速) ");
				else sb.Append($"处理速度={ToSpeed(dataMap["speed"])}倍速 ");

				sb.Append($"视频时长={ToTimeSpan(dataMap["time"]).ToFriendlyDisplayShort()} ");
				sb.Append($"码率={(ToSize(dataMap["bitrate"])).ToSizeDescription(1)}/s");
			}
			else
			{
				sb.Append($"编码结果 => 视频大小:{ToSize(dataMap["video"]).ToSizeDescription(1)}, ");
				sb.Append($"音频大小={(ToSize(dataMap["audio"])).ToSizeDescription(1)}");
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

				if (await CombineAsync(tg.list, tmpFile, true).ConfigureAwait(false))
				{
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

					if (DeleteAfterCombine)
						DeleteFiles(tg.list);
					else RenameFiles(tg.list);
				}
				else
				{
					Console.WriteLine("- 合并失败！");
					if (File.Exists(tmpFile))
						File.Delete(tmpFile);
				}
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
