using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BuyList.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuyList.Tests.Web;

public sealed class ImportPageTests : IClassFixture<ImportPageTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"buylist-import-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:BuyList"] = $"Data Source={_db}" }));
        }
    }

    private const string Header = "retailer,url,ean,mpn,brand,title,price,vat_included,stock\n";
    private readonly Factory _factory;
    public ImportPageTests(Factory f) => _factory = f;

    private async Task<HttpClient> LoggedInClient()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var html = await (await c.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Antiforgery(html), ["Username"] = "admin", ["Password"] = "changeme"
        }));
        return c;
    }

    private static string Antiforgery(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<string> Preview(HttpClient c, string csv)
    {
        var page = await (await c.GetAsync("/Import")).Content.ReadAsStringAsync();
        var form = new MultipartFormDataContent
        {
            { new StringContent(Antiforgery(page)), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "Upload", "offers.csv" }
        };
        var res = await c.PostAsync("/Import?handler=Preview", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Import_page_requires_login()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Import")).StatusCode);
    }

    [Fact]
    public async Task Preview_lists_rejected_rows_with_reasons_and_writes_nothing()
    {
        var c = await LoggedInClient();
        var before = Count();
        var html = await Preview(c, Header +
            "Shop,https://a.example/preview-1,5012345678900,M-1,Acme,Good,5.00,false,\n" +
            "Shop,,5012345678900,M-2,Acme,No link,5.00,false,\n" +
            "Shop,http://a.example/insecure,,,,Insecure,5.00,false,\n");

        Assert.Contains("1 valid", html);
        Assert.Contains("2 rejected", html);
        Assert.Contains("url is required", html);
        Assert.Contains("must start with https://", html);
        Assert.Equal(before, Count());   // preview never saves
    }

    [Fact]
    public async Task Confirming_imports_valid_rows_and_offers_the_rejected_download()
    {
        var c = await LoggedInClient();
        var html = await Preview(c, Header +
            "Shop,https://a.example/confirm-1,5012345678900,M-1,Acme,Good,5.00,false,in stock\n" +
            "Shop,,,,,No link,5.00,false,\n");
        var token = Regex.Match(html, "name=\"token\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var res = await c.PostAsync("/Import?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Antiforgery(html), ["token"] = token
        }));
        var done = await res.Content.ReadAsStringAsync();

        Assert.Contains("Import complete", done);
        Assert.Contains("url is required", done);
        using (var scope = _factory.Services.CreateScope())
            Assert.Contains(scope.ServiceProvider.GetRequiredService<BuyListDbContext>().SourceOffers,
                o => o.SourceUrl == "https://a.example/confirm-1");

        var csv = await (await c.GetAsync($"/Import?handler=Rejected&token={token}")).Content.ReadAsStringAsync();
        Assert.Contains("url is required", csv);
    }

    [Fact]
    public async Task Source_links_open_in_a_new_tab_safely()
    {
        var c = await LoggedInClient();
        var html = await Preview(c, Header + "Shop,https://a.example/link-1,,,,T,5.00,false,\n");
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public async Task Stale_or_unknown_token_is_handled_gracefully()
    {
        var c = await LoggedInClient();
        var page = await (await c.GetAsync("/Import")).Content.ReadAsStringAsync();
        var res = await c.PostAsync("/Import?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Antiforgery(page), ["token"] = "nope" }));
        Assert.Contains("preview has expired", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Non_csv_uploads_are_refused()
    {
        var c = await LoggedInClient();
        var page = await (await c.GetAsync("/Import")).Content.ReadAsStringAsync();
        var form = new MultipartFormDataContent
        {
            { new StringContent(Antiforgery(page)), "__RequestVerificationToken" },
            { new ByteArrayContent(new byte[] { 1, 2, 3 }), "Upload", "evil.exe" }
        };
        var html = await (await c.PostAsync("/Import?handler=Preview", form)).Content.ReadAsStringAsync();
        Assert.Contains("Only .csv files", html);
    }

    [Fact]
    public async Task Sample_file_downloads()
    {
        var c = await LoggedInClient();
        var res = await c.GetAsync("/Import?handler=Sample");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("retailer,url", await res.Content.ReadAsStringAsync());
    }

    private int Count()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BuyListDbContext>().SourceOffers.Count();
    }
}
