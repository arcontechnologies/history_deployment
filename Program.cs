using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using ClosedXML.Excel;
using System.IO;

namespace ArtifactDeploymentsApp
{
    class Program
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient httpClient = new HttpClient();
        
        // Configuration values
        private static string connectionString;
        private static string apiUrl;
        private static string tableName;
        private static string xlsxFilePath;
        private static int pageSize;
        private static int maxRetries;
        private static int retryDelayMs;

        static async Task<int> Main(string[] args)
        {
            try
            {
                logger.Info("Application started");
                LoadConfiguration();

                // Parse command line arguments
                if (args.Length == 0)
                {
                    ShowHelp();
                    return 0;
                }

                var command = args[0].ToLower();

                switch (command)
                {
                    case "--create":
                        logger.Info("Creating SQL table...");
                        CreateSqlTable();
                        logger.Info("SQL table creation completed");
                        break;

                    case "--history":
                        logger.Info("Starting history load (will delete existing data)...");
                        DeleteAllRecords();
                        await LoadHistory();
                        logger.Info("History load completed");
                        break;

                    case "--daily":
                        logger.Info("Starting daily operations...");
                        await LoadDaily();
                        await SaveToXlsx();
                        logger.Info("Daily operations completed");
                        break;

                    case "--save":
                        logger.Info("Exporting data to Excel...");
                        await SaveToXlsx();
                        logger.Info("Excel export completed");
                        break;

                    case "--help":
                    case "-h":
                        ShowHelp();
                        break;

                    default:
                        logger.Error($"Unknown command: {args[0]}");
                        ShowHelp();
                        return -1;
                }

                logger.Info("Application completed successfully");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Application failed");
                return -1;
            }
            finally
            {
                httpClient?.Dispose();
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Artifact Deployments Application");
            Console.WriteLine("================================");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ArtifactDeploymentsApp [command]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  --create    Create SQL Server table");
            Console.WriteLine("  --history   Delete all records and load complete history from API");
            Console.WriteLine("  --daily     Load today's data and export to Excel");
            Console.WriteLine("  --save      Export current database data to Excel file");
            Console.WriteLine("  --help      Show this help information");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  ArtifactDeploymentsApp --create");
            Console.WriteLine("  ArtifactDeploymentsApp --history");
            Console.WriteLine("  ArtifactDeploymentsApp --daily");
            Console.WriteLine("  ArtifactDeploymentsApp --save");
            Console.WriteLine();
            Console.WriteLine("Configuration is managed through App.config file.");
            Console.WriteLine();
        }

        private static void LoadConfiguration()
        {
            logger.Info("Loading configuration");
            
            var dbServer = ConfigurationManager.AppSettings["DbServer"];
            var database = ConfigurationManager.AppSettings["Database"];
            connectionString = $"Server={dbServer};Database={database};Integrated Security=true;Connection Timeout=120;";
            
            apiUrl = ConfigurationManager.AppSettings["ApiUrl"];
            tableName = ConfigurationManager.AppSettings["TableName"];
            xlsxFilePath = ConfigurationManager.AppSettings["XlsxFilePath"];
            pageSize = int.Parse(ConfigurationManager.AppSettings["PageSize"]);
            maxRetries = int.Parse(ConfigurationManager.AppSettings["MaxRetries"]);
            retryDelayMs = int.Parse(ConfigurationManager.AppSettings["RetryDelayMs"]);

            logger.Info($"Configuration loaded - Table: {tableName}, PageSize: {pageSize}");
        }
        private static void CreateSqlTable()
        {
            logger.Info("Creating SQL table if not exists");
            
            var createTableScript = $@"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='{tableName}' AND xtype='U')
                CREATE TABLE {tableName} (
                    [ArtifactGroup] VARCHAR(1000),
                    [ArtifactId] VARCHAR(1000),
                    [ArtifactVersion] VARCHAR(1000),
                    [CustomId] VARCHAR(1000),
                    [DeployedOn] VARCHAR(1000),
                    [DeploymentURL] VARCHAR(1000),
                    [Environment] VARCHAR(1000),
                    [Id] VARCHAR(1000),
                    [TargetPlatform] VARCHAR(1000),
                    [Version] VARCHAR(1000),
                    -- Complete TargetDetails as JSON (for new/unknown platforms)
                    [TargetDetails] VARCHAR(1000),
                    -- Helios platform TargetDetails fields
                    [TargetDetails_appArtifactID] VARCHAR(1000),
                    [TargetDetails_appGroupID] VARCHAR(1000),
                    [TargetDetails_appVersion] VARCHAR(1000),
                    [TargetDetails_confArtifactID] VARCHAR(1000),
                    [TargetDetails_confGroupID] VARCHAR(1000),
                    [TargetDetails_confVersion] VARCHAR(1000),
                    [TargetDetails_deployScope] VARCHAR(1000),
                    [TargetDetails_landscape] VARCHAR(1000),
                    -- Marvin platform TargetDetails fields
                    [TargetDetails_compSpec] VARCHAR(1000),
                    [TargetDetails_compSpecVersion] VARCHAR(1000),
                    [TargetDetails_logic_env] VARCHAR(1000),
                    [TargetDetails_platform] VARCHAR(1000),
                    -- WCM platform TargetDetails fields
                    [TargetDetails_ChannelName] VARCHAR(1000),
                    [TargetDetails_TechPlatform] VARCHAR(1000),
                    [TargetDetails_app_code] VARCHAR(1000),
                    [TargetDetails_service] VARCHAR(1000)
                )";

            ExecuteSqlCommand(createTableScript);
            logger.Info("SQL table creation completed");
        }
        private static async Task LoadHistory()
        {
            logger.Info("Starting history load");
            
            int offset = 0;
            bool isLastPage = false;
            int totalRecordsProcessed = 0;

            while (!isLastPage)
            {
                try
                {
                    logger.Info($"Processing page with offset: {offset}");
                    
                    var apiResponse = await GetApiDataWithRetry(offset);
                    if (apiResponse?.List == null || !apiResponse.List.Any())
                    {
                        logger.Warn("No data received from API");
                        break;
                    }

                    await BulkInsertData(apiResponse.List);
                    
                    totalRecordsProcessed += apiResponse.List.Count;
                    isLastPage = apiResponse.PageInfo?.IsLastPage ?? true;
                    offset += pageSize;

                    logger.Info($"Processed {apiResponse.List.Count} records. Total: {totalRecordsProcessed}");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Error processing offset {offset}");
                    throw;
                }
            }

            logger.Info($"History load completed. Total records: {totalRecordsProcessed}");
        }
        private static async Task LoadDaily()
        {
            logger.Info("Starting daily load");
            
            var currentDate = DateTime.Now.Date;
            
            // Check if data already exists for current date
            if (HasDataForDate(currentDate))
            {
                logger.Info($"Data already exists for {currentDate:yyyy-MM-dd}. Skipping daily load.");
                return;
            }
            
            int offset = 0;
            int totalRecordsProcessed = 0;
            bool continueLoading = true;

            // First, delete any existing records for current date (cleanup)
            DeleteCurrentDateRecords(currentDate);

            while (continueLoading)
            {
                try
                {
                    logger.Info($"Processing daily page with offset: {offset}");
                    
                    var apiResponse = await GetApiDataWithRetry(offset);
                    if (apiResponse?.List == null || !apiResponse.List.Any())
                    {
                        break;
                    }

                    var todayRecords = apiResponse.List
                        .Where(x => x.DeployedOn.HasValue && x.DeployedOn.Value.Date == currentDate)
                        .ToList();

                    if (todayRecords.Any())
                    {
                        await BulkInsertData(todayRecords);
                        totalRecordsProcessed += todayRecords.Count;
                    }

                    // Check if we've passed today's records
                    var hasOlderRecords = apiResponse.List
                        .Any(x => x.DeployedOn.HasValue && x.DeployedOn.Value.Date < currentDate);
                    
                    if (hasOlderRecords || apiResponse.PageInfo?.IsLastPage == true)
                    {
                        continueLoading = false;
                    }
                    else
                    {
                        offset += pageSize;
                    }

                    logger.Info($"Processed {todayRecords.Count} today's records. Total today: {totalRecordsProcessed}");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"Error processing daily offset {offset}");
                    throw;
                }
            }

            logger.Info($"Daily load completed. Total records for {currentDate:yyyy-MM-dd}: {totalRecordsProcessed}");
        }

        private static bool HasDataForDate(DateTime date)
        {
            logger.Info($"Checking if data exists for {date:yyyy-MM-dd}");
            
            var dateString = date.ToString("yyyy-MM-dd");
            var checkQuery = $@"
                SELECT COUNT(1) 
                FROM {tableName} 
                WHERE [DeployedOn] LIKE @DatePattern";

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(checkQuery, connection))
                {
                    command.Parameters.AddWithValue("@DatePattern", dateString + "%");
                    var count = (int)command.ExecuteScalar();
                    
                    logger.Info($"Found {count} existing records for {date:yyyy-MM-dd}");
                    return count > 0;
                }
            }
        }
        private static async Task<ApiResponse> GetApiDataWithRetry(int offset)
        {
            int attempt = 0;
            while (attempt < maxRetries)
            {
                try
                {
                    var url = $"{apiUrl}?limit={pageSize}&offset={offset}";
                    logger.Debug($"Calling API: {url}");
                    
                    var response = await httpClient.GetStringAsync(url);
                    
                    // Log first 500 characters of response for debugging
                    logger.Debug($"API Response (first 500 chars): {response.Substring(0, Math.Min(response.Length, 500))}");
                    
                    var settings = new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore,
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                        Error = HandleDeserializationError
                    };
                    
                    var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(response, settings);
                    
                    logger.Debug($"API call successful. Received {apiResponse?.List?.Count ?? 0} records");
                    return apiResponse;
                }
                catch (Exception ex)
                {
                    attempt++;
                    logger.Error(ex, $"API call failed (attempt {attempt}/{maxRetries})");
                    
                    if (attempt >= maxRetries)
                    {
                        logger.Fatal($"API call failed after {maxRetries} attempts");
                        throw;
                    }
                    
                    await Task.Delay(retryDelayMs);
                }
            }
            
            return null;
        }

        private static void HandleDeserializationError(object sender, Newtonsoft.Json.Serialization.ErrorEventArgs e)
        {
            logger.Warn($"JSON Deserialization warning at path '{e.ErrorContext.Path}': {e.ErrorContext.Error.Message}");
            e.ErrorContext.Handled = true; // Continue processing
        }
        private static async Task BulkInsertData(List<ArtifactDeploymentData> artifacts)
        {
            logger.Info($"Starting bulk insert for {artifacts.Count} records");
            
            var dataTable = CreateDataTable();
            
            foreach (var artifact in artifacts)
            {
                var row = dataTable.NewRow();
                PopulateDataRow(row, artifact);
                dataTable.Rows.Add(row);
            }

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                
                using (var bulkCopy = new SqlBulkCopy(connection)
                {
                    DestinationTableName = tableName,
                    BulkCopyTimeout = 300
                })
                {
                    // Map columns
                    foreach (DataColumn column in dataTable.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    }
                    
                    await bulkCopy.WriteToServerAsync(dataTable);
                }
            }
            
            logger.Info("Bulk insert completed");
        }
        private static DataTable CreateDataTable()
        {
            var dataTable = new DataTable();
            
            // Add columns based on denormalized JSON structure - all VARCHAR(1000)
            dataTable.Columns.Add("ArtifactGroup", typeof(string));
            dataTable.Columns.Add("ArtifactId", typeof(string));
            dataTable.Columns.Add("ArtifactVersion", typeof(string));
            dataTable.Columns.Add("CustomId", typeof(string));
            dataTable.Columns.Add("DeployedOn", typeof(string));
            dataTable.Columns.Add("DeploymentURL", typeof(string));
            dataTable.Columns.Add("Environment", typeof(string));
            dataTable.Columns.Add("Id", typeof(string));
            dataTable.Columns.Add("TargetPlatform", typeof(string));
            dataTable.Columns.Add("Version", typeof(string));
            
            // Complete TargetDetails as JSON (for future/unknown platforms)
            dataTable.Columns.Add("TargetDetails", typeof(string));
            
            // Denormalized TargetDetails columns for known platforms
            // Helios platform fields
            dataTable.Columns.Add("TargetDetails_appArtifactID", typeof(string));
            dataTable.Columns.Add("TargetDetails_appGroupID", typeof(string));
            dataTable.Columns.Add("TargetDetails_appVersion", typeof(string));
            dataTable.Columns.Add("TargetDetails_confArtifactID", typeof(string));
            dataTable.Columns.Add("TargetDetails_confGroupID", typeof(string));
            dataTable.Columns.Add("TargetDetails_confVersion", typeof(string));
            dataTable.Columns.Add("TargetDetails_deployScope", typeof(string));
            dataTable.Columns.Add("TargetDetails_landscape", typeof(string));
            
            // Marvin platform fields
            dataTable.Columns.Add("TargetDetails_compSpec", typeof(string));
            dataTable.Columns.Add("TargetDetails_compSpecVersion", typeof(string));
            dataTable.Columns.Add("TargetDetails_logic_env", typeof(string));
            dataTable.Columns.Add("TargetDetails_platform", typeof(string));
            
            // WCM platform fields
            dataTable.Columns.Add("TargetDetails_ChannelName", typeof(string));
            dataTable.Columns.Add("TargetDetails_TechPlatform", typeof(string));
            dataTable.Columns.Add("TargetDetails_app_code", typeof(string));
            dataTable.Columns.Add("TargetDetails_service", typeof(string));
            
            return dataTable;
        }
        private static void PopulateDataRow(DataRow row, ArtifactDeploymentData artifact)
        {
            row["ArtifactGroup"] = artifact.ArtifactGroup ?? string.Empty;
            row["ArtifactId"] = artifact.ArtifactId ?? string.Empty;
            row["ArtifactVersion"] = artifact.ArtifactVersion ?? string.Empty;
            row["CustomId"] = artifact.CustomId ?? string.Empty;
            row["DeployedOn"] = artifact.DeployedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
            row["DeploymentURL"] = artifact.DeploymentURL ?? string.Empty;
            row["Environment"] = artifact.Environment ?? string.Empty;
            row["Id"] = artifact.Id?.ToString() ?? string.Empty;
            row["TargetPlatform"] = artifact.TargetPlatform ?? string.Empty;
            row["Version"] = artifact.Version ?? string.Empty;
            
            // Store complete TargetDetails as JSON (for future/unknown platforms)
            row["TargetDetails"] = ConvertToString(artifact.TargetDetails);
            
            // Denormalize TargetDetails object into separate columns for known platforms
            var targetDetails = ExtractTargetDetails(artifact.TargetDetails);
            
            // Check for unknown platforms and log them
            var knownPlatforms = new[] { "Helios", "Marvin", "WCM" };
            if (!string.IsNullOrEmpty(artifact.TargetPlatform) && 
                !knownPlatforms.Contains(artifact.TargetPlatform, StringComparer.OrdinalIgnoreCase))
            {
                logger.Warn($"Unknown TargetPlatform encountered: '{artifact.TargetPlatform}'. " +
                           $"TargetDetails: {ConvertToString(artifact.TargetDetails)}. " +
                           $"Consider updating the application to support this platform.");
            }
            
            // Helios platform fields
            row["TargetDetails_appArtifactID"] = targetDetails.GetValueOrDefault("appArtifactID", string.Empty);
            row["TargetDetails_appGroupID"] = targetDetails.GetValueOrDefault("appGroupID", string.Empty);
            row["TargetDetails_appVersion"] = targetDetails.GetValueOrDefault("appVersion", string.Empty);
            row["TargetDetails_confArtifactID"] = targetDetails.GetValueOrDefault("confArtifactID", string.Empty);
            row["TargetDetails_confGroupID"] = targetDetails.GetValueOrDefault("confGroupID", string.Empty);
            row["TargetDetails_confVersion"] = targetDetails.GetValueOrDefault("confVersion", string.Empty);
            row["TargetDetails_deployScope"] = targetDetails.GetValueOrDefault("deployScope", string.Empty);
            row["TargetDetails_landscape"] = targetDetails.GetValueOrDefault("landscape", string.Empty);
            
            // Marvin platform fields
            row["TargetDetails_compSpec"] = targetDetails.GetValueOrDefault("compSpec", string.Empty);
            row["TargetDetails_compSpecVersion"] = targetDetails.GetValueOrDefault("compSpecVersion", string.Empty);
            row["TargetDetails_logic_env"] = targetDetails.GetValueOrDefault("logic_env", string.Empty);
            row["TargetDetails_platform"] = targetDetails.GetValueOrDefault("platform", string.Empty);
            
            // WCM platform fields
            row["TargetDetails_ChannelName"] = targetDetails.GetValueOrDefault("ChannelName", string.Empty);
            row["TargetDetails_TechPlatform"] = targetDetails.GetValueOrDefault("TechPlatform", string.Empty);
            row["TargetDetails_app_code"] = targetDetails.GetValueOrDefault("app_code", string.Empty);
            row["TargetDetails_service"] = targetDetails.GetValueOrDefault("service", string.Empty);
            
            // Log if there are unknown fields in TargetDetails that we're not capturing
            var knownFields = new[]
            {
                // Helios fields
                "appArtifactID", "appGroupID", "appVersion", "confArtifactID", "confGroupID", 
                "confVersion", "deployScope", "landscape",
                // Marvin fields  
                "compSpec", "compSpecVersion", "logic_env", "platform",
                // WCM fields
                "ChannelName", "TechPlatform", "app_code", "service"
            };
            
            var unknownFields = targetDetails.Keys.Where(k => !knownFields.Contains(k) && k != "_raw").ToList();
            if (unknownFields.Any())
            {
                logger.Info($"Unknown TargetDetails fields found for {artifact.TargetPlatform}: {string.Join(", ", unknownFields)}. " +
                           $"ArtifactId: {artifact.ArtifactId}. Consider adding these fields to denormalization.");
            }
        }

        private static Dictionary<string, string> ExtractTargetDetails(object targetDetails)
        {
            var result = new Dictionary<string, string>();
            
            if (targetDetails == null)
                return result;
                
            try
            {
                // If it's already a dictionary/JObject, convert it
                if (targetDetails is Newtonsoft.Json.Linq.JObject jobj)
                {
                    foreach (var prop in jobj.Properties())
                    {
                        result[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                    }
                }
                else
                {
                    // Try to deserialize as JObject
                    var json = targetDetails.ToString();
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
                    foreach (var prop in parsed.Properties())
                    {
                        result[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to extract TargetDetails: {ex.Message}");
                // Fallback: serialize the entire object as JSON string
                result["_raw"] = ConvertToString(targetDetails);
            }
            
            return result;
        }

        private static string ConvertToString(object value)
        {
            if (value == null) return string.Empty;
            
            if (value is string) return (string)value;
            
            // If it's an object, serialize it to JSON string
            try
            {
                return JsonConvert.SerializeObject(value);
            }
            catch
            {
                return value.ToString();
            }
        }

        private static void DeleteCurrentDateRecords(DateTime currentDate)
        {
            logger.Info($"Deleting existing records for {currentDate:yyyy-MM-dd}");
            
            var dateString = currentDate.ToString("yyyy-MM-dd");
            var deleteQuery = $@"
                DELETE FROM {tableName} 
                WHERE [DeployedOn] LIKE @DatePattern";

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(deleteQuery, connection))
                {
                    command.Parameters.AddWithValue("@DatePattern", dateString + "%");
                    var deletedRows = command.ExecuteNonQuery();
                    logger.Info($"Deleted {deletedRows} existing records for current date");
                }
            }
        }

        private static void DeleteAllRecords()
        {
            logger.Info("Deleting all existing records from table");
            
            var deleteQuery = $"DELETE FROM {tableName}";

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(deleteQuery, connection))
                {
                    command.CommandTimeout = 300; // 5 minutes for large deletes
                    var deletedRows = command.ExecuteNonQuery();
                    logger.Info($"Deleted {deletedRows} existing records from table");
                }
            }
        }

        private static async Task SaveToXlsx()
        {
            logger.Info("Starting Excel export");
            
            var query = $"SELECT * FROM {tableName} ORDER BY [DeployedOn] DESC";
            var dataTable = new DataTable();
            
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var adapter = new SqlDataAdapter(query, connection))
                {
                    adapter.Fill(dataTable);
                }
            }

            logger.Info($"Retrieved {dataTable.Rows.Count} records for Excel export");

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("Artifact Deployments");
                worksheet.Cell(1, 1).InsertTable(dataTable.AsEnumerable());
                
                // Format the table
                var table = worksheet.Tables.FirstOrDefault();
                if (table != null)
                {
                    table.Theme = XLTableTheme.TableStyleMedium2;
                }
                
                // Auto-fit columns
                worksheet.Columns().AdjustToContents();
                
                // Ensure directory exists
                var directory = Path.GetDirectoryName(xlsxFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                workbook.SaveAs(xlsxFilePath);
            }

            logger.Info($"Excel file saved to: {xlsxFilePath}");
        }
        private static void ExecuteSqlCommand(string commandText)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(commandText, connection))
                {
                    command.CommandTimeout = 120;
                    command.ExecuteNonQuery();
                }
            }
        }
    }

    // Data model classes
    public class ApiResponse
    {
        [JsonProperty("list")]
        public List<ArtifactDeploymentData> List { get; set; }

        [JsonProperty("pageInfo")]
        public PageInfo PageInfo { get; set; }
    }

    public class PageInfo
    {
        [JsonProperty("isFirstPage")]
        public bool IsFirstPage { get; set; }

        [JsonProperty("isLastPage")]
        public bool IsLastPage { get; set; }

        [JsonProperty("page")]
        public int Page { get; set; }

        [JsonProperty("pageSize")]
        public int PageSize { get; set; }

        [JsonProperty("totalRows")]
        public int TotalRows { get; set; }
    }
    public class ArtifactDeploymentData
    {
        [JsonProperty("ArtifactGroup")]
        public string ArtifactGroup { get; set; }

        [JsonProperty("ArtifactId")]
        public string ArtifactId { get; set; }

        [JsonProperty("ArtifactVersion")]
        public string ArtifactVersion { get; set; }

        [JsonProperty("CustomId")]
        public string CustomId { get; set; }

        [JsonProperty("DeployedOn")]
        public DateTime? DeployedOn { get; set; }

        [JsonProperty("DeploymentURL")]
        public string DeploymentURL { get; set; }

        [JsonProperty("Environment")]
        public string Environment { get; set; }

        [JsonProperty("Id")]
        public int? Id { get; set; }

        [JsonProperty("TargetDetails")]
        public object TargetDetails { get; set; }

        [JsonProperty("TargetPlatform")]
        public string TargetPlatform { get; set; }

        [JsonProperty("Version")]
        public string Version { get; set; }
    }
}
