using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace NotifyAvailableDate
{
    public static class NotifyAvailableDate
    {
        private static HttpClient client;
        private static readonly CookieContainer cookie = new CookieContainer();
        private static readonly HtmlParser parser = new HtmlParser();
        private static readonly List<string> targetColor = new List<string>() { "#FF0000", "#0000FF" };
        private static readonly List<int> targetTimeZoneIndex = new List<int>() { 6, 7, 8, 9, 10, 11, 12 };

        private static IConfigurationRoot Configuration { get; }

        static NotifyAvailableDate()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder()
                            .AddJsonFile("local.settings.json", true)
                            .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        [FunctionName("NotifyAvailableDate")]
        public static void Run([TimerTrigger("0 */10 0-14 * * *")]TimerInfo myTimer, ILogger log)
        {
            try
            {
                log.LogInformation("function start");

                InitializeHttpClient();

                Execute().Wait();
            }
            catch (Exception ex)
            {
                log.LogCritical(ex, "exception");
            }
            finally
            {
                log.LogInformation("function end");
            }
        }

        private static async Task Execute()
        {
            HttpResponseMessage response = await Login();

            var loopCount = 2;

            var bookables = new List<Bookable>();

            for (int i = 1; i <= loopCount; i++)
            {
                Thread.Sleep(1000);

                Stream stream = await response.Content.ReadAsStreamAsync();

                IHtmlDocument document = await parser.ParseDocumentAsync(stream);

                IEnumerable<Bookable> result = Parse(document);

                bookables.AddRange(result);

                if (i != loopCount)
                {
                    response = await NextPage(document);
                }
            }

            if (bookables.Count > 0)
            {
                await Notice(bookables);
            }
        }

        private static async Task<HttpResponseMessage> Login()
        {
            var parameter = await new FormUrlEncodedContent(new Dictionary<string, string>() { { "abc", "Mudjt+z0xsM+brGQYS+1OA==" }, }).ReadAsStringAsync();

            HttpResponseMessage response = await client.GetAsync($"/el25/pc/index.action?{parameter}");

            Stream stream = await response.Content.ReadAsStreamAsync();

            IHtmlDocument document = await parser.ParseDocumentAsync(stream);

            IHtmlCollection<IElement> hiddenFilelds = document.QuerySelector("#p01aForm").QuerySelectorAll("input[type=hidden]");

            var formDatas = hiddenFilelds.ToDictionary(x => x.GetAttribute("name"), y => y.GetAttribute("value"));
            formDatas.Add("b.studentId", Configuration["LoginId"]);
            formDatas.Add("b.password", Configuration["Password"]);
            formDatas.Add("method:doLogin", "");

            return await client.PostAsync("el25/pc/p01a.action", new FormUrlEncodedContent(formDatas));
        }

        private static async Task<HttpResponseMessage> NextPage(IHtmlDocument document)
        {
            IHtmlCollection<IElement> hiddenFilelds = document.QuerySelector("#formId").QuerySelectorAll("input[type=hidden]");

            var formDatas = hiddenFilelds.ToDictionary(x => x.GetAttribute("name"), y => y.GetAttribute("value"));
            formDatas["b.processCd"] = "N";

            return await client.PostAsync("el25/pc/p03a.action", new FormUrlEncodedContent(formDatas));
        }

        private static IEnumerable<Bookable> Parse(IHtmlDocument document)
        {
            foreach (IElement row in document.QuerySelectorAll("table.set")[1].QuerySelectorAll("tr.date"))
            {
                IHtmlCollection<IElement> elements = row.QuerySelectorAll("td");

                IElement dayInfo = elements.ElementAt(0).QuerySelector("font");

                if (!targetColor.Contains(dayInfo?.GetAttribute("color")))
                {
                    continue;
                }

                var day = dayInfo.Text().Replace("\n", "").Replace("\t", "");
                for (int i = 0; i < elements.Count(); i++)
                {
                    IElement element = elements.ElementAt(i);


                    if (!targetTimeZoneIndex.Contains(i) || !element.ClassList.Contains("status1"))
                    {
                        continue;
                    }

                    yield return new Bookable()
                    {
                        Day = day,
                        Time = i
                    };
                }
            }
        }

        private static async Task Notice(List<Bookable> bookables)
        {
            var json = JsonConvert.SerializeObject(new
            {
                username = "日の丸自動車予約システム通知bot",
                text = GetNoticeMessage(bookables)
            });

            var request = new HttpRequestMessage(HttpMethod.Post, Configuration["Slack"]);
            request.Headers.Add("ContentType", "application/json;charset=UTF-8");
            request.Content = new StringContent(json);

            await client.SendAsync(request);
        }

        private static string GetNoticeMessage(List<Bookable> bookables)
        {
            return $@"
以下の日程で予約が可能です。
{string.Join(Environment.NewLine, bookables.Select(x => $"・{x.ToString()}"))}
";
        }

        private static void InitializeHttpClient()
        {
            client = new HttpClient(new HttpClientHandler()
            {
                CookieContainer = cookie,
                UseCookies = true,
            })
            {
                BaseAddress = new Uri("https://www.e-license.jp/")
            };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/47.0.2526.106 Safari/537.36");
        }

        private sealed class Bookable
        {
            public string Day { get; set; }
            public int Time { get; set; }

            public override string ToString()
            {
                return $"{Day} {Time}限";
            }
        }
    }
}
