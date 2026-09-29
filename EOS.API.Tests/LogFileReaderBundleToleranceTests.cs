using System.IO.Compression;
using EOS.API.Features.Diagnostics;
using EOS.API.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 打包要容忍"文件在打包瞬间不可读"：诊断包的价值在于把现场带走，
/// 而轮转/清理/占用都可能让某个日志文件在读取那一刻打不开——
/// 单个文件读不到必须跳过并在清单里如实反映，**不能让整包失败**。
/// 这里用"独占打开"模拟不可读（与轮转窗口内的瞬时占用同类）。
/// </summary>
public sealed class LogFileReaderBundleToleranceTests
{
    [Fact]
    public void 单个文件不可读时打包不抛异常_其余文件仍进包()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eos-bundletest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "api-json.log");
        var provider = new JsonFileLoggerProvider(path, 1024 * 1024, 3, LogLevel.Warning);
        try
        {
            // 两个文件：当前文件稍后被独占占用（不可读），轮转副本可读
            provider.CreateLogger("test").LogWarning("当前文件里的一条 Warning");
            File.WriteAllText(path + ".1", "{\"level\":\"Warning\",\"message\":\"轮转副本\"}" + Environment.NewLine);
        }
        finally
        {
            provider.Dispose();
        }

        try
        {
            // 读取器自带的 provider 会持有写句柄；先释放，才能让下面的独占打开成功
            var readerProvider = new JsonFileLoggerProvider(path, 1024 * 1024, 3, LogLevel.Warning);
            readerProvider.Dispose();
            var reader = new LogFileReader(readerProvider);
            Assert.Equal(2, reader.ListFiles().Count);

            List<(string Name, long SizeBytes)> written;
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // 不得抛异常：不可读的那个文件应被跳过
                    written = reader.WriteToZip(archive).ToList();
                }
                Assert.True(exclusive.Length >= 0);
            }

            // 可读的副本进了包，不可读的当前文件被跳过
            Assert.Contains(written, item => item.Name == "api-json.log.1");
            Assert.DoesNotContain(written, item => item.Name == "api-json.log");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 目录内没有任何日志时打包返回空清单而不是报错()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eos-bundletest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "api-json.log");
        var reader = new LogFileReader(new JsonFileLoggerProvider(path, 1024 * 1024, 3, LogLevel.Warning));
        // 构造后立即删掉文件，模拟"打包时目录是空的"
        foreach (var file in reader.ListFiles()) File.Delete(file.FullName);

        using var buffer = new MemoryStream();
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true);
        var written = reader.WriteToZip(archive);
        Assert.Empty(written);
        Directory.Delete(dir, recursive: true);
    }
}
