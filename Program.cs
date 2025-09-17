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

namespace PlatformInventoryApp
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
            Console.WriteLine("Platform Inventory Application");
            Console.WriteLine("=============================");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  PlatformInventoryApp [command]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  --create    Create SQL Server table");
            Console.WriteLine("  --history   Delete all records and load complete history from API");
            Console.WriteLine("  --daily     Load today's data and export to Excel");
            Console.WriteLine("  --save      Export current database data to Excel file");
            Console.WriteLine("  --help      Show this help information");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  PlatformInventoryApp --create");
            Console.WriteLine("  PlatformInventoryApp --history");
            Console.WriteLine("  PlatformInventoryApp --daily");
            Console.WriteLine("  PlatformInventoryApp --save");
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
                    [ApplicationCode] VARCHAR(1000),
                    [ApplicationCodeAUID] VARCHAR(1000),
                    [Code] VARCHAR(1000),
                    [ComponentType] VARCHAR(1000),
                    [Description] VARCHAR(1000),
                    [GitLabProjectId] VARCHAR(1000),
                    [GitLabProjectInformation] VARCHAR(1000),
                    [GitLabProjectName] VARCHAR(1000),
                    [GitLabProjectSource] VARCHAR(1000),
                    [GitLabProjectWebURL] VARCHAR(1000),
                    [ComponentId] VARCHAR(1000),
                    [IsGitLabProjectArchived] VARCHAR(1000),
                    [IsOnboarded] VARCHAR(1000),
                    [LastActivityOn] VARCHAR(1000),
                    [MappingAsSource] VARCHAR(1000),
                    [MappingAsTarget] VARCHAR(1000),
                    [OnboardedOn] VARCHAR(1000),
                    [OnboardingCandidate] VARCHAR(1000),
                    [OnboardingStatus] VARCHAR(1000),
                    [OwnerUnitId] VARCHAR(1000),
                    [OwnerUnitName] VARCHAR(1000),
                    [OwnerUnitType] VARCHAR(1000),
                    [PlatformHome] VARCHAR(1000),
                    [PlatformName] VARCHAR(1000),
                    [ScanProjectCode] VARCHAR(1000),
                    [SquadSecurityCode] VARCHAR(1000),
                    [TargetName] VARCHAR(1000),
                    [TribeSecurityCode] VARCHAR(1000),
                    [Type] VARCHAR(1000),
                    [UnitContributionUnitIds] VARCHAR(1000),
                    [UnitContributionUnitNames] VARCHAR(1000),
                    [UnitContributionUnitTypes] VARCHAR(1000)
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
                        .Where(x => x.LastActivityOn.HasValue && x.LastActivityOn.Value.Date == currentDate)
                        .ToList();

                    if (todayRecords.Any())
                    {
                        await BulkInsertData(todayRecords);
                        totalRecordsProcessed += todayRecords.Count;
                    }

                    // Check if we've passed today's records
                    var hasOlderRecords = apiResponse.List
                        .Any(x => x.LastActivityOn.HasValue && x.LastActivityOn.Value.Date < currentDate);
                    
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
                WHERE [LastActivityOn] LIKE @DatePattern";

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
        private static async Task BulkInsertData(List<ComponentData> components)
        {
            logger.Info($"Starting bulk insert for {components.Count} records");
            
            var dataTable = CreateDataTable();
            
            foreach (var component in components)
            {
                var row = dataTable.NewRow();
                PopulateDataRow(row, component);
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
            
            // Add columns based on JSON structure - all VARCHAR(1000)
            dataTable.Columns.Add("ApplicationCode", typeof(string));
            dataTable.Columns.Add("ApplicationCodeAUID", typeof(string));
            dataTable.Columns.Add("Code", typeof(string));
            dataTable.Columns.Add("ComponentType", typeof(string));
            dataTable.Columns.Add("Description", typeof(string));
            dataTable.Columns.Add("GitLabProjectId", typeof(string));
            dataTable.Columns.Add("GitLabProjectInformation", typeof(string));
            dataTable.Columns.Add("GitLabProjectName", typeof(string));
            dataTable.Columns.Add("GitLabProjectSource", typeof(string));
            dataTable.Columns.Add("GitLabProjectWebURL", typeof(string));
            dataTable.Columns.Add("ComponentId", typeof(string));
            dataTable.Columns.Add("IsGitLabProjectArchived", typeof(string));
            dataTable.Columns.Add("IsOnboarded", typeof(string));
            dataTable.Columns.Add("LastActivityOn", typeof(string));
            dataTable.Columns.Add("MappingAsSource", typeof(string));
            dataTable.Columns.Add("MappingAsTarget", typeof(string));
            dataTable.Columns.Add("OnboardedOn", typeof(string));
            dataTable.Columns.Add("OnboardingCandidate", typeof(string));
            dataTable.Columns.Add("OnboardingStatus", typeof(string));
            dataTable.Columns.Add("OwnerUnitId", typeof(string));
            dataTable.Columns.Add("OwnerUnitName", typeof(string));
            dataTable.Columns.Add("OwnerUnitType", typeof(string));
            dataTable.Columns.Add("PlatformHome", typeof(string));
            dataTable.Columns.Add("PlatformName", typeof(string));
            dataTable.Columns.Add("ScanProjectCode", typeof(string));
            dataTable.Columns.Add("SquadSecurityCode", typeof(string));
            dataTable.Columns.Add("TargetName", typeof(string));
            dataTable.Columns.Add("TribeSecurityCode", typeof(string));
            dataTable.Columns.Add("Type", typeof(string));
            dataTable.Columns.Add("UnitContributionUnitIds", typeof(string));
            dataTable.Columns.Add("UnitContributionUnitNames", typeof(string));
            dataTable.Columns.Add("UnitContributionUnitTypes", typeof(string));
            
            return dataTable;
        }
        private static void PopulateDataRow(DataRow row, ComponentData component)
        {
            row["ApplicationCode"] = component.ApplicationCode ?? string.Empty;
            row["ApplicationCodeAUID"] = component.ApplicationCodeAUID ?? string.Empty;
            row["Code"] = component.Code ?? string.Empty;
            row["ComponentType"] = ConvertToString(component.ComponentType);
            row["Description"] = ConvertToString(component.Description);
            row["GitLabProjectId"] = component.GitLabProjectId?.ToString() ?? string.Empty;
            row["GitLabProjectInformation"] = component.GitLabProjectInformation ?? string.Empty;
            row["GitLabProjectName"] = component.GitLabProjectName ?? string.Empty;
            row["GitLabProjectSource"] = component.GitLabProjectSource ?? string.Empty;
            row["GitLabProjectWebURL"] = component.GitLabProjectWebURL ?? string.Empty;
            row["ComponentId"] = component.Id?.ToString() ?? string.Empty;
            row["IsGitLabProjectArchived"] = component.IsGitLabProjectArchived ?? string.Empty;
            row["IsOnboarded"] = component.IsOnboarded?.ToString() ?? string.Empty;
            row["LastActivityOn"] = component.LastActivityOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
            row["MappingAsSource"] = component.MappingAsSource?.ToString() ?? string.Empty;
            row["MappingAsTarget"] = component.MappingAsTarget?.ToString() ?? string.Empty;
            row["OnboardedOn"] = component.OnboardedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
            row["OnboardingCandidate"] = component.OnboardingCandidate?.ToString() ?? string.Empty;
            row["OnboardingStatus"] = component.OnboardingStatus ?? string.Empty;
            row["OwnerUnitId"] = component.OwnerUnitId?.ToString() ?? string.Empty;
            row["OwnerUnitName"] = component.OwnerUnitName ?? string.Empty;
            row["OwnerUnitType"] = component.OwnerUnitType ?? string.Empty;
            row["PlatformHome"] = component.PlatformHome ?? string.Empty;
            row["PlatformName"] = component.PlatformName ?? string.Empty;
            row["ScanProjectCode"] = component.ScanProjectCode ?? string.Empty;
            row["SquadSecurityCode"] = component.SquadSecurityCode ?? string.Empty;
            row["TargetName"] = component.TargetName ?? string.Empty;
            row["TribeSecurityCode"] = component.TribeSecurityCode ?? string.Empty;
            row["Type"] = component.Type ?? string.Empty;            
            // Flatten arrays to comma-separated strings
            row["UnitContributionUnitIds"] = component.UnitContributionUnitId != null && component.UnitContributionUnitId.Any() 
                ? string.Join(",", component.UnitContributionUnitId) : string.Empty;
            row["UnitContributionUnitNames"] = component.UnitContributionUnitName != null && component.UnitContributionUnitName.Any() 
                ? string.Join(",", component.UnitContributionUnitName) : string.Empty;
            row["UnitContributionUnitTypes"] = component.UnitContributionUnitType != null && component.UnitContributionUnitType.Any() 
                ? string.Join(",", component.UnitContributionUnitType) : string.Empty;
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
                WHERE [LastActivityOn] LIKE @DatePattern";

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
            
            var query = $"SELECT * FROM {tableName} ORDER BY [LastActivityOn] DESC";
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
                var worksheet = workbook.Worksheets.Add("Platform Inventory");
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
        public List<ComponentData> List { get; set; }

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
    public class ComponentData
    {
        [JsonProperty("ApplicationCode")]
        public string ApplicationCode { get; set; }

        [JsonProperty("ApplicationCodeAUID")]
        public string ApplicationCodeAUID { get; set; }

        [JsonProperty("Code")]
        public string Code { get; set; }

        [JsonProperty("ComponentType")]
        public object ComponentType { get; set; }

        [JsonProperty("Description")]
        public object Description { get; set; }

        [JsonProperty("GitLabProjectId")]
        public int? GitLabProjectId { get; set; }

        [JsonProperty("GitLabProjectInformation")]
        public string GitLabProjectInformation { get; set; }

        [JsonProperty("GitLabProjectName")]
        public string GitLabProjectName { get; set; }

        [JsonProperty("GitLabProjectSource")]
        public string GitLabProjectSource { get; set; }

        [JsonProperty("GitLabProjectWebURL")]
        public string GitLabProjectWebURL { get; set; }

        [JsonProperty("Id")]
        public int? Id { get; set; }

        [JsonProperty("IsGitLabProjectArchived")]
        public string IsGitLabProjectArchived { get; set; }
        [JsonProperty("IsOnboarded")]
        public int? IsOnboarded { get; set; }

        [JsonProperty("LastActivityOn")]
        public DateTime? LastActivityOn { get; set; }

        [JsonProperty("MappingAsSource")]
        public int? MappingAsSource { get; set; }

        [JsonProperty("MappingAsTarget")]
        public int? MappingAsTarget { get; set; }

        [JsonProperty("OnboardedOn")]
        public DateTime? OnboardedOn { get; set; }

        [JsonProperty("OnboardingCandidate")]
        public int? OnboardingCandidate { get; set; }

        [JsonProperty("OnboardingStatus")]
        public string OnboardingStatus { get; set; }

        [JsonProperty("OwnerUnitId")]
        public int? OwnerUnitId { get; set; }

        [JsonProperty("OwnerUnitName")]
        public string OwnerUnitName { get; set; }

        [JsonProperty("OwnerUnitType")]
        public string OwnerUnitType { get; set; }

        [JsonProperty("PlatformHome")]
        public string PlatformHome { get; set; }

        [JsonProperty("PlatformName")]
        public string PlatformName { get; set; }

        [JsonProperty("ScanProjectCode")]
        public string ScanProjectCode { get; set; }

        [JsonProperty("SquadSecurityCode")]
        public string SquadSecurityCode { get; set; }

        [JsonProperty("TargetName")]
        public string TargetName { get; set; }
        [JsonProperty("TribeSecurityCode")]
        public string TribeSecurityCode { get; set; }

        [JsonProperty("Type")]
        public string Type { get; set; }

        [JsonProperty("UnitContributionUnitId")]
        public List<int> UnitContributionUnitId { get; set; }

        [JsonProperty("UnitContributionUnitName")]
        public List<string> UnitContributionUnitName { get; set; }

        [JsonProperty("UnitContributionUnitType")]
        public List<string> UnitContributionUnitType { get; set; }
    }
}