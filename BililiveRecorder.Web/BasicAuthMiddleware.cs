using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace BililiveRecorder.Web
{
    public class BasicAuthMiddleware
    {
        private readonly RequestDelegate next;
        private readonly CompositeFileProvider fileProvider;
        private const string BasicAndSpace = "Basic ";

        private static string? Html401Page;

        public BasicAuthMiddleware(RequestDelegate next, CompositeFileProvider fileProvider)
        {
            this.next = next ?? throw new ArgumentNullException(nameof(next));
            this.fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        }

        public Task InvokeAsync(HttpContext context)
        {
            if (context.RequestServices.GetService<BasicAuthCredential>() is not { } credential)
            {
                // 没有启用身份验证
                return this.next(context);
            }

            string? headerValue = context.Request.Headers["Authorization"];
            if (string.IsNullOrEmpty(headerValue) ||
                !headerValue.StartsWith(BasicAndSpace, StringComparison.OrdinalIgnoreCase))
            {
                return this.ResponseWith401Async(context);
            }

            var requestCredential = headerValue[BasicAndSpace.Length..].Trim();

            if (string.IsNullOrEmpty(requestCredential))
            {
                return this.ResponseWith401Async(context);
            }

            if (CredentialEqualsFixedTime(credential.EncoededValue, requestCredential))
            {
                return this.next(context);
            }
            else
            {
                return this.ResponseWith401Async(context);
            }
        }

        /// <summary>
        /// 常量时间比较凭据，避免通过字符串比较的耗时差异逐字节猜测密码。
        /// </summary>
        private static bool CredentialEqualsFixedTime(string expected, string actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);

            if (expectedBytes.Length != actualBytes.Length)
            {
                // 长度不同时也要做一次等长比较，避免通过失败耗时泄露长度信息
                CryptographicOperations.FixedTimeEquals(expectedBytes, expectedBytes);
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private async Task ResponseWith401Async(HttpContext context)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "text/html";
            context.Response.Headers.Append(HeaderNames.WWWAuthenticate, $"{BasicAndSpace}realm=\"BililiveRecorder {GitVersionInformation.FullSemVer}\"");

            if (Html401Page is null)
            {
                using var file = this.fileProvider.GetFileInfo("/401.html").CreateReadStream();
                using var reader = new StreamReader(file);
                var str = await reader.ReadToEndAsync().ConfigureAwait(false);
                Html401Page = str.Replace("__VERSION__", GitVersionInformation.FullSemVer).Replace("__FULL_VERSION__", GitVersionInformation.InformationalVersion);
            }

            await context.Response.WriteAsync(Html401Page, System.Text.Encoding.UTF8).ConfigureAwait(false);
        }
    }
}
