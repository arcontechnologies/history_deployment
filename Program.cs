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
        private static string componentsTableName;
        private static string xlsxFilePath;
        private static int pageSize;
        private static int maxRetries;
        private static int retryDelayMs;
        private static int delayBetweenChunksMs;

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

                    case "--update-components":
                        logger.Info("Starting component inventory update from deployment data...");
                        await UpdateComponentsFromDeployments();
                        logger.Info("Component inventory update completed");
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
            Console.WriteLine("  --create             Create SQL Server table");
            Console.WriteLine("  --history            Delete all records and load complete history from API");
            Console.WriteLine("  --daily              Load today's data and export to Excel");
            Console.WriteLine("  --save               Export current database data to Excel file");
            Console.WriteLine("  --update-components  Update component inventory with deployment data");
            Console.WriteLine("  --help               Show this help information");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  ArtifactDeploymentsApp --create");
            Console.WriteLine("  ArtifactDeploymentsApp --history");
            Console.WriteLine("  ArtifactDeploymentsApp --daily");
            Console.WriteLine("  ArtifactDeploymentsApp --update-components");
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
            
            // Fix the configuration reading - explicitly set correct values
            tableName = ConfigurationManager.AppSettings["TableName"];
            componentsTableName = ConfigurationManager.AppSettings["ComponentsTableName"];
            
            // Debug what we're actually reading
            logger.Info($"Raw config - TableName: '{ConfigurationManager.AppSettings["TableName"]}'");
            logger.Info($"Raw config - ComponentsTableName: '{ConfigurationManager.AppSettings["ComponentsTableName"]}'");
            
            // Force correct values if config is wrong
            if (string.IsNullOrEmpty(tableName) || tableName.Contains("PLATFORM_INVENTORY"))
            {
                tableName = "[DMAS].[dbo].[TB_ODS_ARTIFACT_DEPLOYMENTS]";
                logger.Warn("Fixed tableName to deployments table");
            }
            
            if (string.IsNullOrEmpty(componentsTableName) || !componentsTableName.Contains("PLATFORM_INVENTORY"))
            {
                componentsTableName = "[DMAS].[dbo].[TB_ODS_PLATFORM_INVENTORY]";
                logger.Warn("Fixed componentsTableName to inventory table");
            }
            
            xlsxFilePath = ConfigurationManager.AppSettings["XlsxFilePath"];
            pageSize = int.Parse(ConfigurationManager.AppSettings["PageSize"]);
            maxRetries = int.Parse(ConfigurationManager.AppSettings["MaxRetries"]);
            retryDelayMs = int.Parse(ConfigurationManager.AppSettings["RetryDelayMs"]);
            delayBetweenChunksMs = int.Parse(ConfigurationManager.AppSettings["DelayBetweenChunksMs"] ?? "1000");

            logger.Info($"Configuration loaded - Deployments Table: {tableName}, Components Table: {componentsTableName}, PageSize: {pageSize}, Delay: {delayBetweenChunksMs}ms");
        }

        private static async Task UpdateComponentsFromDeployments()
        {
            logger.Info("Starting SIMPLE fix for empty denormalized TargetDetails fields");
            
            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();
                    
                    // Show status before fix
                    logger.Info("=== STATUS BEFORE FIX ===");
                    ShowEmptyFieldsStatus(connection);
                    
                    // STEP 1: Fix MARVIN records with empty denormalized fields
                    logger.Info("Step 1: Fixing MARVIN records...");
                    var marvinSql = $@"
                        UPDATE {componentsTableName}
                        SET 
                            [TargetDetails_compSpec] = CASE 
                                WHEN ([TargetDetails_compSpec] IS NULL OR [TargetDetails_compSpec] = '') 
                                     AND [TargetDetails] LIKE '%compSpec%'
                                THEN JSON_VALUE([TargetDetails], '$.compSpec')
                                ELSE [TargetDetails_compSpec]
                            END,
                            [TargetDetails_platform] = CASE 
                                WHEN ([TargetDetails_platform] IS NULL OR [TargetDetails_platform] = '') 
                                     AND [TargetDetails] LIKE '%platform%'
                                THEN JSON_VALUE([TargetDetails], '$.platform')
                                ELSE [TargetDetails_platform]
                            END
                        WHERE [TargetPlatform] = 'Marvin'
                          AND ([TargetDetails_compSpec] IS NULL OR [TargetDetails_compSpec] = '' 
                               OR [TargetDetails_platform] IS NULL OR [TargetDetails_platform] = '')
                          AND [TargetDetails] IS NOT NULL AND [TargetDetails] != ''";
                    
                    using (var cmd = new SqlCommand(marvinSql, connection))
                    {
                        cmd.CommandTimeout = 300;
                        var marvinUpdated = await cmd.ExecuteNonQueryAsync();
                        logger.Info($"✓ Updated {marvinUpdated} MARVIN records");
                    }
                    
                    // STEP 2: Fix WCM records with empty denormalized fields
                    logger.Info("Step 2: Fixing WCM records...");
                    var wcmSql = $@"
                        UPDATE {componentsTableName}
                        SET 
                            [TargetDetails_ChannelName] = CASE 
                                WHEN ([TargetDetails_ChannelName] IS NULL OR [TargetDetails_ChannelName] = '') 
                                     AND [TargetDetails] LIKE '%ChannelName%'
                                THEN JSON_VALUE([TargetDetails], '$.ChannelName')
                                ELSE [TargetDetails_ChannelName]
                            END,
                            [TargetDetails_TechPlatform] = CASE 
                                WHEN ([TargetDetails_TechPlatform] IS NULL OR [TargetDetails_TechPlatform] = '') 
                                     AND [TargetDetails] LIKE '%TechPlatform%'
                                THEN JSON_VALUE([TargetDetails], '$.TechPlatform')
                                ELSE [TargetDetails_TechPlatform]
                            END
                        WHERE [TargetPlatform] = 'WCM'
                          AND ([TargetDetails_ChannelName] IS NULL OR [TargetDetails_ChannelName] = '' 
                               OR [TargetDetails_TechPlatform] IS NULL OR [TargetDetails_TechPlatform] = '')
                          AND [TargetDetails] IS NOT NULL AND [TargetDetails] != ''";
                    
                    using (var cmd = new SqlCommand(wcmSql, connection))
                    {
                        cmd.CommandTimeout = 300;
                        var wcmUpdated = await cmd.ExecuteNonQueryAsync();
                        logger.Info($"✓ Updated {wcmUpdated} WCM records");
                    }
                    
                    // Show status after fix
                    logger.Info("=== STATUS AFTER FIX ===");
                    ShowEmptyFieldsStatus(connection);
                }
                
                logger.Info("Denormalization fix completed successfully");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Denormalization fix failed");
                throw;
            }
        }

        private static void ShowEmptyFieldsStatus(SqlConnection connection)
        {
            var statusSql = $@"
                SELECT 
                    [TargetPlatform],
                    COUNT(*) as TotalRecords,
                    COUNT(CASE WHEN [TargetDetails_compSpec] IS NOT NULL AND [TargetDetails_compSpec] != '' THEN 1 END) as MarvinCompSpec,
                    COUNT(CASE WHEN [TargetDetails_platform] IS NOT NULL AND [TargetDetails_platform] != '' THEN 1 END) as MarvinPlatform,
                    COUNT(CASE WHEN [TargetDetails_ChannelName] IS NOT NULL AND [TargetDetails_ChannelName] != '' THEN 1 END) as WcmChannelName,
                    COUNT(CASE WHEN [TargetDetails_TechPlatform] IS NOT NULL AND [TargetDetails_TechPlatform] != '' THEN 1 END) as WcmTechPlatform
                FROM {componentsTableName}
                WHERE [TargetPlatform] IN ('Helios', 'Marvin', 'WCM')
                GROUP BY [TargetPlatform]
                ORDER BY [TargetPlatform]";
            
            using (var cmd = new SqlCommand(statusSql, connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var platform = reader["TargetPlatform"].ToString();
                    var total = reader["TotalRecords"];
                    var marvinCompSpec = reader["MarvinCompSpec"];
                    var marvinPlatform = reader["MarvinPlatform"];
                    var wcmChannelName = reader["WcmChannelName"];
                    var wcmTechPlatform = reader["WcmTechPlatform"];
                    
                    logger.Info($"{platform,7}: {total,3} total");
                    if (platform == "Marvin")
                    {
                        logger.Info($"         compSpec: {marvinCompSpec,3}/{total,3}, platform: {marvinPlatform,3}/{total,3}");
                    }
                    else if (platform == "WCM")
                    {
                        logger.Info($"         ChannelName: {wcmChannelName,3}/{total,3}, TechPlatform: {wcmTechPlatform,3}/{total,3}");
                    }
                }
            }
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
                    
                    // Add delay between chunks to avoid overwhelming the server
                    if (!isLastPage && delayBetweenChunksMs > 0)
                    {
                        logger.Debug($"Waiting {delayBetweenChunksMs}ms before next API call");
                        await Task.Delay(delayBetweenChunksMs);
                    }
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
                    
                    // Add delay between chunks to avoid overwhelming the server
                    if (continueLoading && delayBetweenChunksMs > 0)
                    {
                        logger.Debug($"Waiting {delayBetweenChunksMs}ms before next API call");
                        await Task.Delay(delayBetweenChunksMs);
                    }
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
                    var url = $"{apiUrl}&limit={pageSize}&offset={offset}";
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
            row["TargetDetails_appArtifactID"] = GetValueOrEmpty(targetDetails, "appArtifactID");
            row["TargetDetails_appGroupID"] = GetValueOrEmpty(targetDetails, "appGroupID");
            row["TargetDetails_appVersion"] = GetValueOrEmpty(targetDetails, "appVersion");
            row["TargetDetails_confArtifactID"] = GetValueOrEmpty(targetDetails, "confArtifactID");
            row["TargetDetails_confGroupID"] = GetValueOrEmpty(targetDetails, "confGroupID");
            row["TargetDetails_confVersion"] = GetValueOrEmpty(targetDetails, "confVersion");
            row["TargetDetails_deployScope"] = GetValueOrEmpty(targetDetails, "deployScope");
            row["TargetDetails_landscape"] = GetValueOrEmpty(targetDetails, "landscape");
            
            // Marvin platform fields
            row["TargetDetails_compSpec"] = GetValueOrEmpty(targetDetails, "compSpec");
            row["TargetDetails_compSpecVersion"] = GetValueOrEmpty(targetDetails, "compSpecVersion");
            row["TargetDetails_logic_env"] = GetValueOrEmpty(targetDetails, "logic_env");
            row["TargetDetails_platform"] = GetValueOrEmpty(targetDetails, "platform");
            
            // WCM platform fields
            row["TargetDetails_ChannelName"] = GetValueOrEmpty(targetDetails, "ChannelName");
            row["TargetDetails_TechPlatform"] = GetValueOrEmpty(targetDetails, "TechPlatform");
            row["TargetDetails_app_code"] = GetValueOrEmpty(targetDetails, "app_code");
            row["TargetDetails_service"] = GetValueOrEmpty(targetDetails, "service");
            
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

        private static string GetValueOrEmpty(Dictionary<string, string> dictionary, string key)
        {
            string value;
            return dictionary.TryGetValue(key, out value) ? value : string.Empty;
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
                    logger.Debug($"Extracted {result.Count} fields from JObject: {string.Join(", ", result.Keys)}");
                }
                else if (targetDetails is string jsonString && !string.IsNullOrEmpty(jsonString))
                {
                    // Try to parse JSON string
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(jsonString);
                    foreach (var prop in parsed.Properties())
                    {
                        result[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                    }
                    logger.Debug($"Extracted {result.Count} fields from JSON string: {string.Join(", ", result.Keys)}");
                }
                else
                {
                    // Try to serialize and then deserialize
                    var json = JsonConvert.SerializeObject(targetDetails);
                    var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
                    foreach (var prop in parsed.Properties())
                    {
                        result[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                    }
                    logger.Debug($"Extracted {result.Count} fields from serialized object: {string.Join(", ", result.Keys)}");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to extract TargetDetails: {ex.Message}. Raw value: {targetDetails}");
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

    // New data classes for component correlation
    public class ComponentRecord
    {
        public string Id { get; set; }
        public string Code { get; set; }
        public string ScanProjectCode { get; set; }
        public string TargetName { get; set; }
        public string Type { get; set; }
        public string PlatformName { get; set; }
        public string ComponentType { get; set; }
        public string Description { get; set; }
    }

    public class DeploymentRecord
    {
        public string ArtifactId { get; set; }
        public string TargetPlatform { get; set; }
        public string Environment { get; set; }
        public string CompSpec { get; set; }
        public string CompSpecVersion { get; set; }
        public string LogicEnv { get; set; }
        public string Platform { get; set; }
        public string ChannelName { get; set; }
        public string TechPlatform { get; set; }
        public string AppCode { get; set; }
        public string Service { get; set; }
        public string DeployedOn { get; set; }
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
