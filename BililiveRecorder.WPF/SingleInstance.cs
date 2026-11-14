using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

#nullable enable
namespace BililiveRecorder.WPF
{
    /// <summary>
    /// 单实例守护：Mutex 判重，命名管道把"再次启动"通知给第一个实例。
    /// （原实现用 .NET Remoting 的 IpcServerChannel，现代 .NET 已移除 Remoting，
    /// 迁移为 System.IO.Pipes。）
    /// </summary>
    public static class SingleInstance
    {
        private static Mutex? singleInstanceMutex;
        private static CancellationTokenSource? serverCts;
        private static Task? serverTask;

        public static event EventHandler? NotificationReceived;

        public static bool CheckMutex(string path)
        {
            var b64path = Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).Replace('+', '_').Replace('/', '-');
            var identifier = "BililiveRecorder:SingeInstance:" + b64path;
            var pipeName = "BililiveRecorder_SI_" + b64path;

            var createdNew = false;
            try
            {
                singleInstanceMutex = new Mutex(true, identifier, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                // 上一个实例崩溃退出时没释放互斥体：此时所有权已转移给本进程，
                // 但构造函数抛出导致字段未赋值，重新打开一个句柄按新建处理
                createdNew = true;
                singleInstanceMutex = new Mutex(false, identifier);
            }

            if (createdNew)
            {
                serverCts = new CancellationTokenSource();
                serverTask = Task.Run(() => PipeServerLoopAsync(pipeName, serverCts.Token));
            }
            else
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                    client.Connect(2000);
                    using var writer = new StreamWriter(client);
                    writer.Write("notify");
                }
                catch
                {
                    // 第一个实例可能正在退出，忽略
                }
            }

            return createdNew;
        }

        public static void Cleanup()
        {
            serverCts?.Cancel();
            serverCts = null;
            try
            {
                serverTask?.Wait(500);
            }
            catch (AggregateException)
            {
            }
            serverTask = null;

            singleInstanceMutex?.Close();
            singleInstanceMutex = null;
        }

        private static void ActivateFirstInstanceCallback() => NotificationReceived?.Invoke(null, EventArgs.Empty);

        private static async Task PipeServerLoopAsync(string pipeName, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    await reader.ReadToEndAsync().ConfigureAwait(false);
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)ActivateFirstInstanceCallback);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // 管道意外失败时稍等重试，保证单实例通知始终可用
                    try { await Task.Delay(1000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
    }
}
