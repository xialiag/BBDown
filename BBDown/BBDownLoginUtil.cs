using QRCoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using static BBDown.BBDownUtil;
using static BBDown.Core.Logger;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using BBDown.Core.Util;

namespace BBDown;

internal static class BBDownLoginUtil
{
    public static async Task<string> GetLoginStatusAsync(string qrcodeKey)
    {
        string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
        return await HTTPUtil.GetWebSourceAsync(queryUrl);
    }

    public static async Task LoginWEB()
    {
        try
        {
            Log("获取登录地址...");
            string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
            string url = JsonDocument.Parse(await HTTPUtil.GetWebSourceAsync(loginUrl)).RootElement.GetProperty("data").GetProperty("url").ToString();
            string qrcodeKey = GetQueryString("qrcode_key", url);
            //Log(oauthKey);
            //Log(url);
            bool flag = false;
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();

            while (true)
            {
                await Task.Delay(1000);
                string w = await GetLoginStatusAsync(qrcodeKey);
                int code = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("code").GetInt32();
                if (code == 86038)
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (code == 86101) //等待扫码
                {
                    continue;
                }
                else if (code == 86090) //等待确认
                {
                    if (!flag)
                    {
                        Log("扫码成功, 请确认...");
                        flag = !flag;
                    }
                }
                else
                {
                    string cc = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("url").ToString();
                    string cookie = await ExtractLoginCookiesAsync(cc);
                    if (string.IsNullOrEmpty(GetCookieValue(cookie, "SESSDATA")))
                    {
                        LogColor("登录失败: 未能换取到SESSDATA, 请重新执行登录指令.");
                        File.Delete("qrcode.png");
                        break;
                    }
                    Log("登录成功: SESSDATA=" + GetCookieValue(cookie, "SESSDATA"));
                    //导出cookie, 转义英文逗号 否则部分场景会出问题
                    await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDown.data"), cookie.Replace(",", "%2C"));
                    File.Delete("qrcode.png");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }

    /// <summary>
    /// 从扫码登录成功返回的 data.url 中提取 Cookie。
    /// 旧格式: SESSDATA 等凭证直接放在 query 参数里, 直接取用。
    /// 新版(2025+): query 只带 ticket, 需携带浏览器 UA 请求该 crossDomain 链接,
    /// 由 302 的 Set-Cookie 响应头下发真正的登录凭证。
    /// </summary>
    private static async Task<string> ExtractLoginCookiesAsync(string loginUrl)
    {
        string[] wanted = ["SESSDATA", "DedeUserID", "DedeUserID__ckMd5", "bili_jct", "sid"];
        var cookies = new Dictionary<string, string>();
        if (loginUrl.Contains('?'))
        {
            foreach (var item in loginUrl[(loginUrl.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = item.IndexOf('=');
                if (eq <= 0) continue;
                cookies[item[..eq]] = item[(eq + 1)..];
            }
        }

        if (cookies.ContainsKey("ticket") && !(cookies.TryGetValue("SESSDATA", out var sessData) && !string.IsNullOrEmpty(sessData)))
        {
            Log("检测到ticket登录, 请求crossDomain换取Cookie...");
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            string current = loginUrl;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(current); i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://passport.bilibili.com/");
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var setCookie in setCookies)
                    {
                        string pair = setCookie.Split(';', 2)[0];
                        int eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        string name = pair[..eq].Trim();
                        if (wanted.Contains(name)) cookies[name] = pair[(eq + 1)..].Trim();
                    }
                }
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
                {
                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location.AbsoluteUri
                        : new Uri(new Uri(current), response.Headers.Location).AbsoluteUri;
                    continue;
                }
                break;
            }
        }

        return string.Join(";", wanted.Where(cookies.ContainsKey).Select(name => $"{name}={cookies[name]}"));
    }

    private static string GetCookieValue(string cookies, string name)
    {
        foreach (var item in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = item.IndexOf('=');
            if (eq > 0 && item[..eq].Trim().Equals(name)) return item[(eq + 1)..].Trim();
        }
        return "";
    }

    public static async Task LoginTV()
    {
        try
        {
            string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
            string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
            var parms = GetTVLoginParms();
            Log("获取登录地址...");
            byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
            string web = Encoding.UTF8.GetString(responseArray);
            string url = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("url").ToString();
            string authCode = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("auth_code").ToString();
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();
            parms.Set("auth_code", authCode);
            parms.Set("ts", GetTimeStamp(true));
            parms.Remove("sign");
            parms.Add("sign", GetSign(ToQueryString(parms)));
            while (true)
            {
                await Task.Delay(1000);
                responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                web = Encoding.UTF8.GetString(responseArray);
                string code = JsonDocument.Parse(web).RootElement.GetProperty("code").ToString();
                if (code == "86038")
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (code == "86039") //等待扫码
                {
                    continue;
                }
                else
                {
                    string cc = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("access_token").ToString();
                    Log("登录成功: AccessToken=" + cc);
                    //导出cookie
                    await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDownTV.data"), "access_token=" + cc);
                    File.Delete("qrcode.png");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }
}