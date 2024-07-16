// See https://aka.ms/new-console-template for more information

using FSLib.Extension;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using VideoRecordingJoiner;

Console.WriteLine(
	$@"监控视频合并工具 by 木魚
============================
可用选项：
  -o	<路径>	指定输出目录; 如果不指定，将默认输出当前运行目录下
  -d		指定合并后删除源文件，如果不指定此选项，则会修改源文件名（防止重复合并）
  -f	<格式>	输出文件名格式，不包含扩展名，支持 yyyy、MM、dd占位符，如 ""yyyy-MM{Path.DirectorySeparatorChar}dd""
  -t	<类型>	输出文件类型，可选 mkv 或 mp4，默认为 mkv，建议使用 mkv
  -gm		按月合并（默认为按日合并）
  -v		详细进度信息输出
  -ie		尝试跳过无法合并的文件继续合并
  -fix		遇到错误媒体文件尝试修复（当前只支持修复 moov atom not found 类型错误文件）

注意：
  - 按月合并模式下，默认输出文件名模板为 【yyyy-MM】，可以使用 -f 选项覆盖
  - 按日合并模式下，默认输出文件名模板为 【yyyy-MM{Path.DirectorySeparatorChar}dd】，可以使用 -f 选项覆盖
");

var worker = new JoinWorker();
for (var i = 0; i < args.Length; i++)
{
	switch (args[i])
	{
		case "-o":
			worker.Target = args[++i];
			break;
		case "-d":
			worker.DeleteAfterCombine = true;
			break;
		case "-v":
			worker.Verbose = true;
			break;
		case "-gm":
			worker.IsGroupByMonth = true;
			break;
		case "-f":
			worker.FileNameFormat = args[++i];
			break;
		case "-t":
			var type = args[++i];
			if (type is "mkv" or "mp4")
				worker.FileType = type;
			else
			{
				Console.WriteLine("错误： -t 指定的格式需要为 mp4 或 mkv 其中之一。");
				return;
			}
			break;
		case "-fix":
			worker.TryFixMoovAtom = true;
			break;
		case "-ie":
			worker.IgnoreErrorFile = true;
			break;
		default:
			worker.SourceFiles.Add(args[i]);
			break;
	}
}

if (!await worker.CheckFfmpegAsync().ConfigureAwait(false))
{
	Console.WriteLine($"错误：未检测到 ffempg。本程序依赖ffmpeg。请确保相关软件包已安装，或当前目录下存在 {worker.FfmpegCmd()} 且可正常执行。");
	return;
}
if (worker.TryFixMoovAtom)
{
	if (!await Untrunc.EnsureUntruncOkAsync().ConfigureAwait(false))
	{
		Console.WriteLine($"错误：未检测到 untrunc。修复功能依赖 untrunc。请确保相关软件包已安装，或当前目录下存在 untrunc 且可正常执行。");
		return;
	}
}
await worker.JoinAsync().ConfigureAwait(false);