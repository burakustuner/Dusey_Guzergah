using DuseyGuzergah.Api.Sessions;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DesignSessionOptions>(builder.Configuration.GetSection(DesignSessionOptions.SectionName));
builder.Services.AddSingleton<IDesignSessionStore, FileDesignSessionStore>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Vite", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var maxUpload = builder.Configuration.GetValue<long?>("Sessions:MaxUploadBytes") ?? 52_428_800;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUpload;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUpload;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors("Vite");
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();

var spaDist = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "web", "dist"));
if (Directory.Exists(spaDist))
{
    var spaFiles = new PhysicalFileProvider(spaDist);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = spaFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = spaFiles });
    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(spaDist, "index.html"));
    });
}

app.Run();

public partial class Program;
