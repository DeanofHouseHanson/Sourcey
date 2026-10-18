using System.Text;
using BuyList.Application.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;

namespace BuyList.Web.Pages.Import;

[RequestSizeLimit(MaxUploadBytes + 100_000)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes + 100_000)]
public class IndexModel : PageModel
{
    public const int MaxUploadBytes = 5_000_000;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    private readonly OfferFileValidator _validator;
    private readonly IOfferImportService _importer;
    private readonly IMemoryCache _cache;

    public IndexModel(OfferFileValidator validator, IOfferImportService importer, IMemoryCache cache)
    {
        _validator = validator;
        _importer = importer;
        _cache = cache;
    }

    [BindProperty] public IFormFile? Upload { get; set; }

    public string? Error { get; private set; }
    public ImportPreview? Preview { get; private set; }
    public ImportSummary? Result { get; private set; }
    public IReadOnlyList<RejectedRow> RejectedAfterImport { get; private set; } = Array.Empty<RejectedRow>();
    public string? Token { get; private set; }
    public string? FileName { get; private set; }

    public void OnGet() { }

    // Step 1: validate only. Nothing is written to the database.
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (Upload is null || Upload.Length == 0) { Error = "Choose a CSV file to upload."; return Page(); }
        if (Upload.Length > MaxUploadBytes) { Error = "That file is larger than 5 MB."; return Page(); }
        if (!Path.GetExtension(Upload.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        { Error = "Only .csv files are accepted."; return Page(); }

        await using var ms = new MemoryStream();
        await Upload.CopyToAsync(ms);
        var bytes = ms.ToArray();

        Preview = _validator.Validate(new MemoryStream(bytes));
        FileName = Path.GetFileName(Upload.FileName);
        if (Preview.FatalError is not null) { Error = Preview.FatalError; return Page(); }

        Token = Guid.NewGuid().ToString("N");
        _cache.Set(Key(Token), bytes, PreviewLifetime);
        return Page();
    }

    // Step 2: re-validate the exact bytes previewed (never trust the client), then import.
    public async Task<IActionResult> OnPostImportAsync(string token, CancellationToken ct)
    {
        if (!_cache.TryGetValue<byte[]>(Key(token), out var bytes) || bytes is null)
        { Error = "The preview has expired. Upload the file again."; return Page(); }

        var preview = _validator.Validate(new MemoryStream(bytes));
        if (!preview.CanImport) { Error = preview.FatalError ?? "There are no valid rows to import."; return Page(); }

        Result = await _importer.ImportAsync(preview.Valid, preview.Rejected.Count, ct);
        RejectedAfterImport = preview.Rejected;
        _cache.Remove(Key(token));
        if (preview.Rejected.Count > 0)
            _cache.Set(RejectedKey(token), Encoding.UTF8.GetBytes(OfferFileValidator.RejectedToCsv(preview.Rejected)), PreviewLifetime);
        Token = token;
        return Page();
    }

    public IActionResult OnGetRejected(string token) =>
        _cache.TryGetValue<byte[]>(RejectedKey(token), out var csv) && csv is not null
            ? File(Encoding.UTF8.GetPreamble().Concat(csv).ToArray(), "text/csv", "rejected-rows.csv")
            : NotFound();

    public IActionResult OnGetSample(string name)
    {
        var file = name == "invalid" ? "offers.invalid.csv" : "offers.sample.csv";
        var path = Path.Combine(AppContext.BaseDirectory, "samples", file);
        return System.IO.File.Exists(path) ? PhysicalFile(path, "text/csv", file) : NotFound();
    }

    private static string Key(string token) => "import-preview:" + token;
    private static string RejectedKey(string token) => "import-rejected:" + token;
}
