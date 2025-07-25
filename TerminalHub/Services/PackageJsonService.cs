using System.Text.Json;
using TerminalHub.Models;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    public class PackageJsonService : IPackageJsonService
    {
        private readonly ILogger<PackageJsonService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public PackageJsonService(ILogger<PackageJsonService> logger)
        {
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
        }

        public async Task<PackageJsonInfo?> LoadPackageJsonAsync(string folderPath)
        {
            try
            {
                var packageJsonPath = Path.Combine(folderPath, "package.json");
                
                if (!File.Exists(packageJsonPath))
                {
                    _logger.LogDebug($"package.json not found in {folderPath}");
                    return null;
                }

                var jsonContent = await File.ReadAllTextAsync(packageJsonPath);
                var fileInfo = new FileInfo(packageJsonPath);

                var documentOptions = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                using var doc = JsonDocument.Parse(jsonContent, documentOptions);
                var root = doc.RootElement;

                var packageInfo = new PackageJsonInfo
                {
                    FilePath = packageJsonPath,
                    LastModified = fileInfo.LastWriteTime
                };

                // name プロパティの取得
                if (root.TryGetProperty("name", out var nameElement))
                {
                    packageInfo.Name = nameElement.GetString();
                }

                // version プロパティの取得
                if (root.TryGetProperty("version", out var versionElement))
                {
                    packageInfo.Version = versionElement.GetString();
                }

                // scripts セクションの解析
                if (root.TryGetProperty("scripts", out var scriptsElement) && scriptsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var script in scriptsElement.EnumerateObject())
                    {
                        var scriptName = script.Name;
                        var scriptCommand = script.Value.GetString();
                        
                        if (!string.IsNullOrEmpty(scriptCommand))
                        {
                            packageInfo.Scripts[scriptName] = scriptCommand;
                        }
                    }
                }

                _logger.LogInformation($"Loaded package.json from {folderPath}: {packageInfo.Scripts.Count} scripts found");
                return packageInfo;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, $"Failed to parse package.json in {folderPath}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load package.json from {folderPath}");
                return null;
            }
        }

        public async Task<List<TaskInfo>> GetTasksAsync(string folderPath)
        {
            var tasks = new List<TaskInfo>();
            var packageInfo = await LoadPackageJsonAsync(folderPath);

            if (packageInfo == null || packageInfo.Scripts.Count == 0)
            {
                return tasks;
            }

            foreach (var script in packageInfo.Scripts)
            {
                tasks.Add(new TaskInfo
                {
                    Name = script.Key,
                    Command = script.Value,
                    Description = GenerateDescription(script.Key, script.Value)
                });
            }

            return tasks.OrderBy(t => t.Name).ToList();
        }

        private string GenerateDescription(string scriptName, string command)
        {
            // 一般的なスクリプト名から説明を生成
            return scriptName.ToLower() switch
            {
                "build" => "プロジェクトをビルド",
                "start" => "アプリケーションを起動",
                "test" => "テストを実行",
                "dev" or "develop" => "開発サーバーを起動",
                "lint" => "コードの構文チェック",
                "format" => "コードフォーマット",
                "watch" => "ファイル変更を監視",
                "clean" => "ビルド成果物をクリーンアップ",
                _ => $"npm run {scriptName}"
            };
        }
    }
}