using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VideoRecordingJoiner
{
	using System.Diagnostics;
	using System.IO.Compression;
	using System.Net;
	using System.Runtime.InteropServices;

	using FSLib.Extension;

	internal class Untrunc
	{
		static string _binName;

		/// <summary>
		/// 确认工具可用
		/// </summary>
		/// <returns></returns>
		public static async Task<bool> EnsureUntruncOkAsync()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				_binName  = "untrunc.exe";
			else _binName = "untrunc";

			if (CheckExists())
				return true;

			if (OperatingSystem.IsWindows())
			{
				_binName = Path.Combine(ApplicationRunTimeContext.GetProcessMainModuleDirectory(), "bin", "untrunc.exe");

				if (CheckExists())
					return true;
			}

			Console.WriteLine($"指定了自动修复错误的视频文件，但未找到 untrunc 可执行文件。正在自动下载，请稍等...");
			if (!await DownloadPackageAsync().ConfigureAwait(false))
			{
				Console.WriteLine("错误：未能成功下载所需的工具包。");
				return false;
			}
			if (!CheckExists())
			{
				Console.WriteLine("错误：未能检测到所需所需的工具包。");
				return false;
			}
			return true;
		}

		static async Task<bool> DownloadPackageAsync()
		{
			if (!Environment.Is64BitOperatingSystem)
			{
				Console.WriteLine("错误：工具未提供非 64bit 二进制文件下载源，请手动下载安装。");
				return false;
			}

			try
			{
				if (OperatingSystem.IsWindows())
					return await DownloadWindowsPackageAsync().ConfigureAwait(false);
				return await DownloadLinuxPackageAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"无法下载包：{ex.Message}");
				return false;
			}
		}

		static async Task<bool> DownloadLinuxPackageAsync()
		{
			var targetFile = Path.Combine(Path.GetTempPath(), "untrunc.gz");
			if (!await DownloadPackageAsync("https://alist.fishlee.net/p/bin-utils/linux-amd64/untrunc.gz", targetFile).ConfigureAwait(false))
				return false;

			var targetBin = Path.Combine(ApplicationRunTimeContext.GetProcessMainModuleDirectory(), _binName);

			await using var fs  = File.Open(targetFile, FileMode.Open);
			await using var gz  = new GZipStream(fs, CompressionMode.Decompress);
			await using var tfs = File.Create(targetBin);

			Console.WriteLine("正在解压缩，请稍候 ...");
			await gz.CopyToAsync(tfs).ConfigureAwait(false);
			tfs.Close();

			var fm = File.GetUnixFileMode(targetBin);
			fm |= UnixFileMode.GroupExecute | UnixFileMode.UserExecute | UnixFileMode.OtherExecute;
			File.SetUnixFileMode(targetBin, fm);

			return true;
		}

		static async Task<bool> DownloadWindowsPackageAsync()
		{
			var targetFile = Path.Combine(Path.GetTempPath(), "untrunc.exe");
			if (!await DownloadPackageAsync("https://alist.fishlee.net/p/bin-utils/win64/untrunc.exe", targetFile).ConfigureAwait(false))
				return false;

			var psi = new ProcessStartInfo(targetFile)
			{
				WorkingDirectory = ApplicationRunTimeContext.GetProcessMainModuleDirectory()
			};
			await Process.Start(psi)!.WaitForExitAsync().ConfigureAwait(false);
			return true;
		}

		static async Task<bool> DownloadPackageAsync(string url, string toFile)
		{
			Console.WriteLine($"正在从 {url} 下载数据 ...");

			var wc   = new HttpClient();
			var resp = await wc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();

			var length = resp.Content.Headers.ContentLength!.Value;
			var copied = 0;

			await using var fs = File.Create(toFile);

			var buffer  = new byte[0x400].AsMemory();
			var stream  = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
			var readCnt = 0;
			var ts      = new Stopwatch();

			ts.Start();
			var pt = await Task.Factory.StartNew(
					async () =>
					{
						if (length <= 0)
							return;

						var      barLength  = 40;
						var      donePart   = "".PadRight(barLength, '=');
						var      undonePart = "".PadRight(barLength, '-');
						var      pauseTime  = TimeSpan.FromMilliseconds(250);
						var      lenStr     = length.ToSizeDescription();
						TimeSpan esTime;
						double   avgSpeed;

						while (copied < length)
						{
							var p     = copied * 1.0 / length;
							var index = (int)Math.Round(barLength * p);
							esTime   = ts.Elapsed;
							avgSpeed = copied * 1.0 / esTime.TotalSeconds;

							Console.Write($"[{p:P2}] {copied.ToSizeDescription()}/{lenStr} [{donePart[0..index]}>{undonePart[(index)..]}] 耗时 {esTime.ToFriendlyDisplayShort()} 平均速度 {avgSpeed.ToSizeDescription()}/S");

							await Task.Delay(pauseTime).ConfigureAwait(false);
							Console.Write("\u001b[2K\r");
						}
						ts.Stop();
						esTime   = ts.Elapsed;
						avgSpeed = copied * 1.0 / esTime.TotalSeconds;

						Console.WriteLine($"下载完成，耗时 {esTime.ToFriendlyDisplayShort()} 平均速度 {avgSpeed.ToSizeDescription()}/S");
					})
				.ConfigureAwait(false);

			while ((readCnt = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
			{
				copied += readCnt;
				await fs.WriteAsync(buffer.Slice(0, readCnt)).ConfigureAwait(false);
			}
			await pt.ConfigureAwait(false);
			return true;
		}

		static bool CheckExists()
		{
			var psi = new ProcessStartInfo(_binName, "-V")
			{
				UseShellExecute = false,
				CreateNoWindow  = true,
				WindowStyle     = ProcessWindowStyle.Hidden
			};
			try
			{
				Process.Start(psi)!.WaitForExit();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		/// 上个已知的可用视频
		/// </summary>
		public static string _lastKnownWorkingVideo;

		/// <summary>
		/// 记录修复成功列表。主要用于记录最新的参考文件。
		/// </summary>
		/// <param name="srcs"></param>
		public static void LogSucceedCombine(string[] srcs)
		{
			if (_lastKnownWorkingVideo.IsNullOrEmpty())
				_lastKnownWorkingVideo = srcs.Last();
		}

		/// <summary>
		/// 发生错误。这个函数主要用于记录错误文件之前的最后正确文件供修复参考
		/// </summary>
		/// <param name="srcs"></param>
		/// <param name="errFile"></param>
		public static void OnAtomMoovNotFound(string[] srcs, string errFile)
		{
			if (!_lastKnownWorkingVideo.IsNullOrEmpty())
				return;

			var index = srcs.FindIndex(x => string.Compare(x, errFile, StringComparison.OrdinalIgnoreCase) == 0);
			if (index > 0)
				_lastKnownWorkingVideo = srcs[index - 1];
			else if (srcs.Length == 1)
			{
				if (_lastKnownWorkingVideo.IsNullOrEmpty())
				{
					Console.WriteLine($"警告：错误文件是唯一文件，无法修复，请尝试手动修复");
				}
			}
			else
			{
				// 第一个文件
				Console.WriteLine($"警告：错误文件是第一个文件，尝试使用第二个文件作为参考文件，如果连续错误可能无法修复");
				_lastKnownWorkingVideo = srcs[1];
			}
		}

		/// <summary>
		/// 尝试修复视频文件
		/// </summary>
		/// <param name="file"></param>
		/// <returns></returns>
		public static async Task<(string? newFile, string? msg)> TryUntruncVideoFileAsync(string file)
		{
			if (_lastKnownWorkingVideo.IsNullOrEmpty())
				return (null, "无可参考的修复视频，请手动修复");

			var dstFile = file + ".fix";

			Console.WriteLine($"提示：正在尝试自动修复视频文件，错误文件：{file}，参考文件：{_lastKnownWorkingVideo}，修复后文件：{dstFile}");

			var psi = new ProcessStartInfo(_binName, $"-dst \"{dstFile}\" \"{_lastKnownWorkingVideo}\" \"{file}\"")
			{
				UseShellExecute = false,
				CreateNoWindow  = true
			};
			var p = Process.Start(psi);
			await p.WaitForExitAsync().ConfigureAwait(false);

			if (p.ExitCode != 0)
				return ("", $"修复进程意外退出，退出码={p.ExitCode}");

			var fi = new FileInfo(dstFile);
			if (fi.Exists && fi.Length > 0)
				return (dstFile, null);

			return (null, "目标文件不存在，修复失败");
		}

		/// <summary>
		/// 检测当前文件是否被参考文件引用
		/// </summary>
		/// <param name="file"></param>
		/// <returns></returns>
		internal static bool IsReferencedByUntrunc(string file)
		{
			return !_lastKnownWorkingVideo.IsNullOrEmpty() && string.Compare(file, _lastKnownWorkingVideo, StringComparison.OrdinalIgnoreCase) == 0;
		}
	}
}
