using System.IO;

namespace DuseyGuzergah;

public enum ValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>KTB/KSE okuma sonrası özet doğrulama satırı.</summary>
public sealed record FileValidationIssue(
    ValidationSeverity Severity,
    string Source,
    string Message);

/// <summary>KSE/KTB dosyası ve belleğe yüklenen profil koleksiyonları için doğrulama özeti.</summary>
public static class FileLoadValidator
{
    public static List<FileValidationIssue> ValidateTerrainAndKtbPaths(
        string ksePath,
        string ktbPath)
    {
        var list = new List<FileValidationIssue>();
        var kseProvided = !string.IsNullOrWhiteSpace(ksePath);
        var ktbProvided = !string.IsNullOrWhiteSpace(ktbPath);

        if (kseProvided && !File.Exists(ksePath))
        {
            list.Add(new FileValidationIssue(ValidationSeverity.Error, "KSE", $"Dosya bulunamadı: {ksePath}"));
        }

        if (ktbProvided && !File.Exists(ktbPath))
        {
            list.Add(new FileValidationIssue(ValidationSeverity.Error, "KTB", $"Dosya bulunamadı: {ktbPath}"));
        }

        return list;
    }

    public static void AppendTerrainIssues(
        IReadOnlyList<ProfilePoint> terrain,
        ICollection<FileValidationIssue> issues)
    {
        if (terrain.Count == 0)
        {
            return;
        }

        for (var i = 1; i < terrain.Count; i++)
        {
            var d = terrain[i].Station - terrain[i - 1].Station;
            if (Math.Abs(d) < 1e-9)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Warning,
                    "KSE eksen",
                    $"Yakın ya da mükerrer KM: #{i - 1}–#{i} → {terrain[i].Station:0.###} m."));
            }
            else if (d < 0)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Error,
                    "KSE eksen",
                    $"KM azalan sıra #{i}: {terrain[i - 1].Station:0.###} → {terrain[i].Station:0.###} m."));
            }
        }

        var known = terrain.ToList();
        var sortedSta = terrain.Select(p => p.Station).OrderBy(s => s).ToList();
        for (var i = 1; i < known.Count; i++)
        {
            if (Math.Abs(known[i].Station - sortedSta[i]) > 1e-6)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Warning,
                    "KSE eksen",
                    "Kesitler dosyada artan sıra dışında; okuma sırası KM’ye göre sıralı kullanılmış olabilir — kontrol edin."));
                break;
            }
        }
    }

    /// <summary>Ham $DÜŞEY satırı istasyon sırasını KtbReader’a girmeden denetlemek için.</summary>
    public static void AppendKtbRawIssues(
        string path,
        ICollection<FileValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        List<double>? rawBeforeSort = null;
        try
        {
            rawBeforeSort = KtbReader.TryReadVerticalStationOrderRaw(path);
        }
        catch (Exception ex)
        {
            issues.Add(new FileValidationIssue(
                ValidationSeverity.Warning,
                "KTB",
                $"$DÜŞEY okuma: {ex.Message}"));
            return;
        }

        if (rawBeforeSort is null || rawBeforeSort.Count == 0)
        {
            return;
        }

        for (var i = 1; i < rawBeforeSort.Count; i++)
        {
            var ds = rawBeforeSort[i] - rawBeforeSort[i - 1];
            if (ds < -1e-6)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Error,
                    "KTB düşey",
                    $"Dosyada KM sırası bozuk (sıralamadan önce): satır yakını #{i}: {rawBeforeSort[i - 1]:0.###} → {rawBeforeSort[i]:0.###} m."));
                break;
            }

            if (Math.Abs(ds) < 1e-6)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Warning,
                    "KTB düşey",
                    $"Aynı KM’ye yakın iki some (≈ {rawBeforeSort[i]:0.###} m) — sıra #{i}, #{i + 1}."));
                break;
            }
        }

        var sorted = rawBeforeSort.OrderBy(s => s).ToList();
        if (!SequencesAlmostEqual(rawBeforeSort, sorted))
        {
            issues.Add(new FileValidationIssue(
                ValidationSeverity.Info,
                "KTB düşey",
                "Dosyadaki düşey some satırları KM’ye göre otomatik sıralanmış."));
        }
    }

    public static void AppendVertexIssues(
        IReadOnlyList<VerticalAlignmentVertex> vertices,
        ICollection<FileValidationIssue> issues)
    {
        var n = vertices.Count;
        if (n == 0)
        {
            return;
        }

        if (vertices[0].CurveLength > 1e-6 || vertices[^1].CurveLength > 1e-6)
        {
            issues.Add(new FileValidationIssue(
                ValidationSeverity.Warning,
                "KTB düşey",
                "Uç somelerde kurp uzunlığı tipik olarak sıfır — kontrol."));
        }

        for (var i = 1; i < n; i++)
        {
            var ds = vertices[i].Station - vertices[i - 1].Station;
            if (ds <= 1e-6)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Error,
                    "KTB düşey",
                    $"Geçersiz ara mesafe PI #{i}–#{i + 1}: ΔKM = {ds:0.###} m."));
            }
            else if (ds < 1.0 && i > 0 && i < n)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Warning,
                    "KTB düşey",
                    $"Çok kısa teğet aralığı yaklaşık {ds:0.###} m (PI {i + 1}) — düşey çözüm sıkışabilir."));
            }
        }

        for (var i = 1; i < n - 1; i++)
        {
            var v = vertices[i];
            if (v.CurveLength <= 1e-6 &&
                Math.Abs(v.TangentIn) < 1e-6 &&
                Math.Abs(v.TangentOut) < 1e-6 &&
                v.CurveType == 0 &&
                Math.Abs(v.Radius) < 1e-6)
            {
                continue;
            }

            if (v.CurveLength <= 1e-6 &&
                (!(Math.Abs(v.TangentIn) < 1e-6 &&
                   Math.Abs(v.TangentOut) < 1e-6)))
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Warning,
                    "KTB düşey",
                    $"Ara PI #{i + 1} KM {v.Station:0.###}: L≈0 fakat teğet/kurp alanı sıfır değil — dosya tutarılılığını kontrol edin."));
            }

            if (v.CurveType == 2 && Math.Abs(v.Radius) < 1e-3 && v.CurveLength > 1e-6)
            {
                issues.Add(new FileValidationIssue(
                    ValidationSeverity.Warning,
                    "KTB düşey",
                    $"KM {v.Station:0.###}: yay tipinde R çok küçük veya sıfır — yaklaşık çözüm doğrulanmalı."));
            }
        }
    }

    private static bool SequencesAlmostEqual(List<double> a, List<double> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 1e-6)
            {
                return false;
            }
        }

        return true;
    }
}
