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

                // Setup (comment these after first execution)
                CreateSqlTable();
                await LoadHistory();

                // Daily run
                await LoadDaily();
                await SaveToXlsx();

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
                CREATE TABLE [{tableName}] (
                    [Id] INT IDENTITY(1,1) PRIMARY KEY,
                    [ApplicationCode] NVARCHAR(50),
                    [ApplicationCodeAUID] NVARCHAR(50),
                    [Code] NVARCHAR(100),
                    [ComponentType] NVARCHAR(100),
                    [Description] NVARCHAR(MAX),
                    [GitLabProjectId] INT,
                    [GitLabProjectInformation] NVARCHAR(500),
                    [GitLabProjectName] NVARCHAR(200),
                    [GitLabProjectSource] NVARCHAR(100),
                    [GitLabProjectWebURL] NVARCHAR(500),
                    [ComponentId] INT,
                    [IsGitLabProjectArchived] NVARCHAR(10),
                    [IsOnboarded] INT,
                    [LastActivityOn] DATETIME2,
                    [MappingAsSource] INT,
                    [MappingAsTarget] INT,
                    [OnboardedOn] DATETIME2,
                    [OnboardingCandidate] INT,
                    [OnboardingStatus] NVARCHAR(50),
                    [OwnerUnitId] INT,
                    [OwnerUnitName] NVARCHAR(200),
                    [OwnerUnitType] NVARCHAR(100),
                    [PlatformHome] NVARCHAR(500),
                    [PlatformName] NVARCHAR(100),
                    [ScanProjectCode] NVARCHAR(100),
                    [SquadSecurityCode] NVARCHAR(100),
                    [TargetName] NVARCHAR(100),
                    [TribeSecurityCode] NVARCHAR(100),
                    [Type] NVARCHAR(100),
                    [UnitContributionUnitIds] NVARCHAR(MAX),
                    [UnitContributionUnitNames] NVARCHAR(MAX),
                    [UnitContributionUnitTypes] NVARCHAR(MAX),
                    [CreatedDate] DATETIME2 DEFAULT GETDATE(),
                    [UpdatedDate] DATETIME2 DEFAULT GETDATE()
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
            
            var checkQuery = $@"
                SELECT COUNT(1) 
                FROM [{tableName}] 
                WHERE CAST([LastActivityOn] AS DATE) = @Date";

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(checkQuery, connection))
                {
                    command.Parameters.AddWithValue("@Date", date);
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
                    var apiResponse = JsonConvert.DeserializeObject<ApiResponse>(response);
                    
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
                        if (column.ColumnName != "Id") // Skip identity column
                        {
                            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                        }
                    }
                    
                    await bulkCopy.WriteToServerAsync(dataTable);
                }
            }
            
            logger.Info("Bulk insert completed");
        }
        private static DataTable CreateDataTable()
        {
            var dataTable = new DataTable();
            
            // Add columns based on SQL table structure (excluding Id and audit columns)
            dataTable.Columns.Add("ApplicationCode", typeof(string));
            dataTable.Columns.Add("ApplicationCodeAUID", typeof(string));
            dataTable.Columns.Add("Code", typeof(string));
            dataTable.Columns.Add("ComponentType", typeof(string));
            dataTable.Columns.Add("Description", typeof(string));
            dataTable.Columns.Add("GitLabProjectId", typeof(int));
            dataTable.Columns.Add("GitLabProjectInformation", typeof(string));
            dataTable.Columns.Add("GitLabProjectName", typeof(string));
            dataTable.Columns.Add("GitLabProjectSource", typeof(string));
            dataTable.Columns.Add("GitLabProjectWebURL", typeof(string));
            dataTable.Columns.Add("ComponentId", typeof(int));
            dataTable.Columns.Add("IsGitLabProjectArchived", typeof(string));
            dataTable.Columns.Add("IsOnboarded", typeof(int));
            dataTable.Columns.Add("LastActivityOn", typeof(DateTime));
            dataTable.Columns.Add("MappingAsSource", typeof(int));
            dataTable.Columns.Add("MappingAsTarget", typeof(int));
            dataTable.Columns.Add("OnboardedOn", typeof(DateTime));
            dataTable.Columns.Add("OnboardingCandidate", typeof(int));
            dataTable.Columns.Add("OnboardingStatus", typeof(string));
            dataTable.Columns.Add("OwnerUnitId", typeof(int));
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
            row["ApplicationCode"] = component.ApplicationCode ?? (object)DBNull.Value;
            row["ApplicationCodeAUID"] = component.ApplicationCodeAUID ?? (object)DBNull.Value;
            row["Code"] = component.Code ?? (object)DBNull.Value;
            row["ComponentType"] = component.ComponentType ?? (object)DBNull.Value;
            row["Description"] = component.Description ?? (object)DBNull.Value;
            row["GitLabProjectId"] = component.GitLabProjectId.HasValue ? (object)component.GitLabProjectId.Value : DBNull.Value;
            row["GitLabProjectInformation"] = component.GitLabProjectInformation ?? (object)DBNull.Value;
            row["GitLabProjectName"] = component.GitLabProjectName ?? (object)DBNull.Value;
            row["GitLabProjectSource"] = component.GitLabProjectSource ?? (object)DBNull.Value;
            row["GitLabProjectWebURL"] = component.GitLabProjectWebURL ?? (object)DBNull.Value;
            row["ComponentId"] = component.Id.HasValue ? (object)component.Id.Value : DBNull.Value;
            row["IsGitLabProjectArchived"] = component.IsGitLabProjectArchived ?? (object)DBNull.Value;
            row["IsOnboarded"] = component.IsOnboarded.HasValue ? (object)component.IsOnboarded.Value : DBNull.Value;
            row["LastActivityOn"] = component.LastActivityOn.HasValue ? (object)component.LastActivityOn.Value : DBNull.Value;
            row["MappingAsSource"] = component.MappingAsSource.HasValue ? (object)component.MappingAsSource.Value : DBNull.Value;
            row["MappingAsTarget"] = component.MappingAsTarget.HasValue ? (object)component.MappingAsTarget.Value : DBNull.Value;
            row["OnboardedOn"] = component.OnboardedOn.HasValue ? (object)component.OnboardedOn.Value : DBNull.Value;
            row["OnboardingCandidate"] = component.OnboardingCandidate.HasValue ? (object)component.OnboardingCandidate.Value : DBNull.Value;
            row["OnboardingStatus"] = component.OnboardingStatus ?? (object)DBNull.Value;
            row["OwnerUnitId"] = component.OwnerUnitId.HasValue ? (object)component.OwnerUnitId.Value : DBNull.Value;
            row["OwnerUnitName"] = component.OwnerUnitName ?? (object)DBNull.Value;
            row["OwnerUnitType"] = component.OwnerUnitType ?? (object)DBNull.Value;
            row["PlatformHome"] = component.PlatformHome ?? (object)DBNull.Value;
            row["PlatformName"] = component.PlatformName ?? (object)DBNull.Value;
            row["ScanProjectCode"] = component.ScanProjectCode ?? (object)DBNull.Value;
            row["SquadSecurityCode"] = component.SquadSecurityCode ?? (object)DBNull.Value;
            row["TargetName"] = component.TargetName ?? (object)DBNull.Value;
            row["TribeSecurityCode"] = component.TribeSecurityCode ?? (object)DBNull.Value;
            row["Type"] = component.Type ?? (object)DBNull.Value;            
            // Flatten arrays to comma-separated strings
            row["UnitContributionUnitIds"] = component.UnitContributionUnitId != null && component.UnitContributionUnitId.Any() 
                ? string.Join(",", component.UnitContributionUnitId) : (object)DBNull.Value;
            row["UnitContributionUnitNames"] = component.UnitContributionUnitName != null && component.UnitContributionUnitName.Any() 
                ? string.Join(",", component.UnitContributionUnitName) : (object)DBNull.Value;
            row["UnitContributionUnitTypes"] = component.UnitContributionUnitType != null && component.UnitContributionUnitType.Any() 
                ? string.Join(",", component.UnitContributionUnitType) : (object)DBNull.Value;
        }

        private static void DeleteCurrentDateRecords(DateTime currentDate)
        {
            logger.Info($"Deleting existing records for {currentDate:yyyy-MM-dd}");
            
            var deleteQuery = $@"
                DELETE FROM [{tableName}] 
                WHERE CAST([LastActivityOn] AS DATE) = @CurrentDate";

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(deleteQuery, connection))
                {
                    command.Parameters.AddWithValue("@CurrentDate", currentDate);
                    var deletedRows = command.ExecuteNonQuery();
                    logger.Info($"Deleted {deletedRows} existing records for current date");
                }
            }
        }
        private static async Task SaveToXlsx()
        {
            logger.Info("Starting Excel export");
            
            var query = $"SELECT * FROM [{tableName}] ORDER BY [LastActivityOn] DESC";
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
        public string ComponentType { get; set; }

        [JsonProperty("Description")]
        public string Description { get; set; }

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