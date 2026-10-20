// ─────────────────────────────────────────────────────────────
// COM 合同订单管理系统 · Azure 基础设施
// 分三阶段部署（由 deploy.sh 串联）：
//   P1 core  → 网络/存储/SQL/Key Vault/App Service
//   P2 net   → Service Bus/私有端点/私有 DNS/Function/Logic Apps/Cosmos
//   P3 gov   → 预算/告警
// 参数 enablePrivateEndpoints=false 时退化为公网模式，用于排障与低成本演示。
// ─────────────────────────────────────────────────────────────
targetScope = 'resourceGroup'

@description('区域')
param location string = resourceGroup().location
@description('资源名前缀')
param namePrefix string = 'com-poc'
@description('full=支持私有端点的完整版；lite=公网 + 最小规格')
param profile string = 'full'
@description('是否创建私有端点并关闭数据面公网访问')
param enablePrivateEndpoints bool = true
@description('是否部署 Service Bus + Functions 异步链路')
param enableFunctions bool = true
@description('是否部署 Logic Apps 审批编排')
param enableLogicApps bool = true
@description('是否部署 Cosmos DB 审计镜像')
param enableCosmos bool = true
@description('WORM 保留天数（未锁定策略，可解除）')
param wormRetentionDays int = 30
@description('SQL 管理员账号')
param sqlAdminLogin string = 'comadmin'
@secure()
@description('SQL 管理员密码（部署脚本生成）')
param sqlAdminPassword string
@secure()
@description('JWT 签名密钥')
param jwtKey string
@secure()
@description('内部端点共享密钥（Logic Apps → API）')
param internalKey string
@description('部署者对象 ID（用于 Key Vault 访问策略）')
param deployerObjectId string = ''
@description('预算金额（美元/月，0 表示不创建）')
param budgetAmount int = 150

@description('预算统计起始日。utcNow() 只能出现在参数默认值里，因此单独声明。')
param budgetStartDate string = utcNow('yyyy-MM-01')

@description('Cosmos DB 所在区域。East Asia 时常因容量不足返回 ServiceUnavailable，可指向 southeastasia。留空表示跟随 location。')
param cosmosLocation string = ''
@description('成本告警邮箱')
param alertEmail string = ''
@description('演示环境标记')
param environmentName string = 'poc'

var suffix = take(uniqueString(resourceGroup().id), 6)
var isFull = profile == 'full'
var peMode = enablePrivateEndpoints

var webAppName = 'app-${namePrefix}-web-${suffix}'
var apiAppName = 'app-${namePrefix}-api-${suffix}'
var funcAppName = 'func-${namePrefix}-worker-${suffix}'
var webHost = '${webAppName}.azurewebsites.net'
var apiHost = '${apiAppName}.azurewebsites.net'

var tags = {
  app: 'com'
  env: environmentName
  owner: 'azure-training'
  costCenter: 'training-poc'
  managedBy: 'bicep'
}

// ─────────────────────────────────────────────────────────────
// 1. 可观测性底座
// ─────────────────────────────────────────────────────────────
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'law-${namePrefix}-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${namePrefix}-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
    IngestionMode: 'LogAnalytics'
  }
}

// ─────────────────────────────────────────────────────────────
// 2. 网络：Hub-Spoke + 子网 + NSG
// ─────────────────────────────────────────────────────────────
resource vnetHub 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: 'vnet-com-hub'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.10.0.0/16'] }
    subnets: [
      { name: 'snet-shared', properties: { addressPrefix: '10.10.1.0/24' } }
    ]
  }
}

resource nsgApp 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-snet-app'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowHttpsInbound'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourceAddressPrefix: 'Internet', destinationAddressPrefix: '*', sourcePortRange: '*', destinationPortRange: '443' }
      }
      {
        name: 'DenyAllInbound'
        properties: { priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*', sourceAddressPrefix: '*', destinationAddressPrefix: '*', sourcePortRange: '*', destinationPortRange: '*' }
      }
    ]
  }
}

resource nsgPe 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-snet-pe'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowVnetInbound'
        properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: '*', sourceAddressPrefix: 'VirtualNetwork', destinationAddressPrefix: 'VirtualNetwork', sourcePortRange: '*', destinationPortRange: '*' }
      }
    ]
  }
}

resource vnetSpoke 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: 'vnet-com-spoke'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.20.0.0/16'] }
    subnets: [
      {
        name: 'snet-app'
        properties: {
          addressPrefix: '10.20.1.0/24'
          networkSecurityGroup: { id: nsgApp.id }
          delegations: [ { name: 'web', properties: { serviceName: 'Microsoft.Web/serverFarms' } } ]
        }
      }
      {
        name: 'snet-func'
        properties: {
          addressPrefix: '10.20.3.0/26'
          delegations: [ { name: 'web', properties: { serviceName: 'Microsoft.Web/serverFarms' } } ]
        }
      }
      {
        name: 'snet-pe'
        properties: {
          addressPrefix: '10.20.2.0/24'
          networkSecurityGroup: { id: nsgPe.id }
        }
      }
    ]
  }
}

resource peerSpokeToHub 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2023-11-01' = {
  parent: vnetSpoke
  name: 'peer-spoke-to-hub'
  properties: {
    remoteVirtualNetwork: { id: vnetHub.id }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}

resource peerHubToSpoke 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2023-11-01' = {
  parent: vnetHub
  name: 'peer-hub-to-spoke'
  properties: {
    remoteVirtualNetwork: { id: vnetSpoke.id }
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: false
    allowGatewayTransit: false
    useRemoteGateways: false
  }
}

var snetAppId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnetSpoke.name, 'snet-app')
var snetFuncId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnetSpoke.name, 'snet-func')
var snetPeId = resourceId('Microsoft.Network/virtualNetworks/subnets', vnetSpoke.name, 'snet-pe')

// ─────────────────────────────────────────────────────────────
// 3. 存储账户 + WORM 容器
// ─────────────────────────────────────────────────────────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${replace(namePrefix, '-', '')}${suffix}'
  location: location
  tags: tags
  sku: { name: isFull ? 'Standard_LRS' : 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: peMode ? 'Disabled' : 'Enabled'
    networkAcls: peMode ? { defaultAction: 'Deny', bypass: 'AzureServices' } : { defaultAction: 'Allow' }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    isVersioningEnabled: true
  }
}

resource containerContracts 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'contracts'
  properties: { publicAccess: 'None' }
}

resource containerIntake 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'intake'
  properties: { publicAccess: 'None' }
}

// 未锁定策略：可解除，便于课后清理；生产应 lock 并设 3650 天
resource wormPolicy 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: containerContracts
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: wormRetentionDays
    allowProtectedAppendWrites: false
  }
}

// ─────────────────────────────────────────────────────────────
// 4. Key Vault（访问策略模式，避免 RBAC 数据面授权延迟）
// ─────────────────────────────────────────────────────────────
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${namePrefix}-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: tenant().tenantId
    enableRbacAuthorization: false
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: peMode ? 'Disabled' : 'Enabled'
    networkAcls: peMode ? { defaultAction: 'Deny', bypass: 'AzureServices', ipRules: [] } : { defaultAction: 'Allow', bypass: 'AzureServices' }
    accessPolicies: concat(
      deployerObjectId == '' ? [] : [ { tenantId: tenant().tenantId, objectId: deployerObjectId, permissions: { secrets: ['get', 'list', 'set', 'delete', 'purge'] } } ]
    )
  }
}

// ─────────────────────────────────────────────────────────────
// 5. Azure SQL
// ─────────────────────────────────────────────────────────────
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${namePrefix}-${suffix}'
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: peMode ? 'Disabled' : 'Enabled'
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'sqldb-com'
  location: location
  tags: tags
  sku: { name: isFull ? 'S0' : 'Basic', tier: isFull ? 'Standard' : 'Basic' }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 2147483648
    zoneRedundant: false
  }
}

resource sqlFirewallAzure 'Microsoft.Sql/servers/firewallRules@2020-11-01-preview' = if (!peMode) {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDb.name};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=90;Max Pool Size=60;'

resource secretSql 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'sql-connection'
  properties: { value: sqlConnectionString }
}

resource secretJwt 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'jwt-key'
  properties: { value: jwtKey }
}

resource secretInternal 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'internal-key'
  properties: { value: internalKey }
}

// ─────────────────────────────────────────────────────────────
// 6. Service Bus
// ─────────────────────────────────────────────────────────────
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = if (enableFunctions) {
  name: 'sb-${namePrefix}-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    minimumTlsVersion: '1.2'
  }
}

resource queueRender 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = if (enableFunctions) {
  parent: sb
  name: 'contract-render'
  properties: {
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    lockDuration: 'PT2M'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    requiresDuplicateDetection: false
  }
}

resource queueIntake 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = if (enableFunctions) {
  parent: sb
  name: 'intake-ocr'
  properties: {
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    lockDuration: 'PT2M'
  }
}

// ─────────────────────────────────────────────────────────────
// 7. Cosmos DB（审计镜像，Serverless）
// ─────────────────────────────────────────────────────────────
var cosmosRegion = cosmosLocation == '' ? location : cosmosLocation

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = if (enableCosmos) {
  name: 'cosmos-${namePrefix}-${suffix}'
  location: cosmosRegion
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [ { locationName: cosmosRegion, failoverPriority: 0 } ]
    capabilities: [ { name: 'EnableServerless' } ]
    disableLocalAuth: true
    isVirtualNetworkFilterEnabled: false
    publicNetworkAccess: 'Enabled'
    minimalTlsVersion: 'Tls12'
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = if (enableCosmos) {
  parent: cosmos
  name: 'com'
  properties: { resource: { id: 'com' } }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (enableCosmos) {
  parent: cosmosDb
  name: 'audit'
  properties: {
    resource: {
      id: 'audit'
      partitionKey: { paths: [ '/entityId' ], kind: 'Hash' }
      defaultTtl: 2592000
    }
  }
}

// ─────────────────────────────────────────────────────────────
// 8. App Service（一个 S1 计划承载 Web / API / Function 三个应用）
// ─────────────────────────────────────────────────────────────
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${namePrefix}'
  location: location
  tags: tags
  sku: {
    name: isFull ? 'S1' : 'B1'
    tier: isFull ? 'Standard' : 'Basic'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
    zoneRedundant: false
  }
}

var commonAppSettings = [
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  { name: 'ENVIRONMENT_NAME', value: environmentName }
  { name: 'AZURE_REGION', value: location }
  { name: 'APP_VERSION', value: '1.0.0' }
  { name: 'WEBSITE_VNET_ROUTE_ALL', value: '1' }
  { name: 'WEBSITE_DNS_SERVER', value: '168.63.129.16' }
]

resource appApi 'Microsoft.Web/sites@2023-12-01' = {
  name: apiAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      vnetRouteAllEnabled: true
      cors: { allowedOrigins: [ 'https://${webHost}', 'https://${apiHost}' ] }
      appSettings: concat(commonAppSettings, [
        { name: 'APP_ROLE', value: 'api' }
        { name: 'DB_PROVIDER', value: 'sqlserver' }
        { name: 'ConnectionStrings__Default', value: '@Microsoft.KeyVault(VaultName=${kv.name};SecretName=sql-connection)' }
        { name: 'JWT_KEY', value: '@Microsoft.KeyVault(VaultName=${kv.name};SecretName=jwt-key)' }
        { name: 'INTERNAL_KEY', value: '@Microsoft.KeyVault(VaultName=${kv.name};SecretName=internal-key)' }
        { name: 'STORAGE_MODE', value: 'azure' }
        { name: 'STORAGE_BLOB_ENDPOINT', value: storage.properties.primaryEndpoints.blob }
        { name: 'WORM_RETENTION_DAYS', value: string(wormRetentionDays) }
        { name: 'BUS_MODE', value: enableFunctions ? 'servicebus' : 'local' }
        { name: 'SERVICEBUS_FULLY_QUALIFIED_NAMESPACE', value: enableFunctions ? '${sb.name}.servicebus.windows.net' : '' }
        { name: 'COSMOS_ENDPOINT', value: enableCosmos ? cosmos.properties.documentEndpoint : '' }
        { name: 'COSMOS_DATABASE', value: 'com' }
        { name: 'COSMOS_CONTAINER', value: 'audit' }
        { name: 'ENABLE_DEMO_AUTOLOGIN', value: 'true' }
      ])
    }
  }
}

resource appWeb 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: concat(commonAppSettings, [
        { name: 'APP_ROLE', value: 'web' }
        { name: 'DB_PROVIDER', value: 'sqlite' }
        { name: 'ConnectionStrings__Default', value: 'Data Source=/tmp/placeholder.db' }
        { name: 'STORAGE_MODE', value: 'local' }
        { name: 'LOCAL_STORAGE_ROOT', value: '/tmp/storage' }
        { name: 'API_BASE_URL', value: 'https://${apiHost}' }
        { name: 'ENABLE_DEMO_AUTOLOGIN', value: 'true' }
      ])
    }
  }
}

resource appFunc 'Microsoft.Web/sites@2023-12-01' = if (enableFunctions) {
  name: funcAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      vnetRouteAllEnabled: true
      appSettings: concat(commonAppSettings, [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__blobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'AzureWebJobsStorage__queueServiceUri', value: storage.properties.primaryEndpoints.queue }
        { name: 'AzureWebJobsStorage__tableServiceUri', value: storage.properties.primaryEndpoints.table }
        { name: 'ServiceBusConnection__fullyQualifiedNamespace', value: '${sb.name}.servicebus.windows.net' }
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'DB_PROVIDER', value: 'sqlserver' }
        { name: 'ConnectionStrings__Default', value: '@Microsoft.KeyVault(VaultName=${kv.name};SecretName=sql-connection)' }
        { name: 'STORAGE_MODE', value: 'azure' }
        { name: 'STORAGE_BLOB_ENDPOINT', value: storage.properties.primaryEndpoints.blob }
        { name: 'WORM_RETENTION_DAYS', value: string(wormRetentionDays) }
        { name: 'COSMOS_ENDPOINT', value: enableCosmos ? cosmos.properties.documentEndpoint : '' }
      ])
    }
  }
}

// VNet 集成：官方示例写法是 sites/config 子资源且 name 固定为 'virtualNetwork'。
// Bicep 类型定义把 name 收窄成一组字面量，会误报 BCP036，此处显式抑制。
#disable-next-line BCP036
resource apiVnet 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: appApi
  name: 'virtualNetwork'
  properties: {
    subnetResourceId: snetAppId
  }
}

#disable-next-line BCP036
resource funcVnet 'Microsoft.Web/sites/config@2023-12-01' = if (enableFunctions) {
  parent: appFunc
  name: 'virtualNetwork'
  properties: {
    subnetResourceId: snetFuncId
  }
}

// ─────────────────────────────────────────────────────────────
// 9. Logic Apps：审批编排（HttpWebhook 等待回调）
// ─────────────────────────────────────────────────────────────
resource logicApp 'Microsoft.Logic/workflows@2019-05-01' = if (enableLogicApps) {
  name: 'la-${namePrefix}-approval'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      triggers: {
        manual: {
          type: 'Request'
          kind: 'Http'
          inputs: {
            schema: {
              type: 'object'
              properties: {
                taskId: { type: 'string' }
                orderId: { type: 'string' }
                level: { type: 'integer' }
                approverUpn: { type: 'string' }
                amount: { type: 'number' }
              }
            }
          }
        }
      }
      actions: {
        Notify: {
          type: 'Http'
          inputs: {
            method: 'POST'
            uri: 'https://${apiHost}/internal/notify'
            headers: { 'x-internal-key': internalKey, 'Content-Type': 'application/json' }
            body: '@triggerBody()'
            retryPolicy: { type: 'fixed', count: 3, interval: 'PT10S' }
          }
        }
        WaitForDecision: {
          type: 'HttpWebhook'
          runAfter: { Notify: [ 'Succeeded', 'Failed' ] }
          inputs: {
            subscribe: {
              method: 'POST'
              uri: 'https://${apiHost}/internal/webhook/subscribe'
              headers: {
                'x-internal-key': internalKey
                'x-callback-url': '@{listCallbackUrl()}'
                'Content-Type': 'application/json'
              }
              body: '@triggerBody()'
            }
            unsubscribe: {
              method: 'POST'
              uri: 'https://${apiHost}/internal/webhook/unsubscribe'
              headers: { 'x-internal-key': internalKey, 'Content-Type': 'application/json' }
              body: '@triggerBody()'
            }
            timeout: 'PT48H'
          }
        }
        Advance: {
          type: 'Http'
          runAfter: { WaitForDecision: [ 'Succeeded' ] }
          inputs: {
            method: 'POST'
            uri: 'https://${apiHost}/internal/workflow/level-complete'
            headers: { 'x-internal-key': internalKey, 'Content-Type': 'application/json' }
            body: '@triggerBody()'
            retryPolicy: { type: 'fixed', count: 3, interval: 'PT10S' }
          }
        }
      }
    }
  }
}

// ─────────────────────────────────────────────────────────────
// 10. 私有端点 + 私有 DNS
// ─────────────────────────────────────────────────────────────
var dnsZones = [
  { zone: 'privatelink.database.windows.net', group: 'sqlServer', link: 'link-sql' }
  { zone: 'privatelink.blob.core.windows.net', group: 'blob', link: 'link-blob' }
  { zone: 'privatelink.vaultcore.azure.net', group: 'vault', link: 'link-kv' }
]

resource peSql 'Microsoft.Network/privateEndpoints@2023-11-01' = if (peMode) {
  name: 'pe-sql'
  location: location
  tags: tags
  properties: {
    subnet: { id: snetPeId }
    privateLinkServiceConnections: [
      { name: 'pe-sql-conn', properties: { privateLinkServiceId: sqlServer.id, groupIds: [ 'sqlServer' ] } }
    ]
  }
}

resource peBlob 'Microsoft.Network/privateEndpoints@2023-11-01' = if (peMode) {
  name: 'pe-blob'
  location: location
  tags: tags
  properties: {
    subnet: { id: snetPeId }
    privateLinkServiceConnections: [
      { name: 'pe-blob-conn', properties: { privateLinkServiceId: storage.id, groupIds: [ 'blob' ] } }
    ]
  }
}

resource peKv 'Microsoft.Network/privateEndpoints@2023-11-01' = if (peMode) {
  name: 'pe-kv'
  location: location
  tags: tags
  properties: {
    subnet: { id: snetPeId }
    privateLinkServiceConnections: [
      { name: 'pe-kv-conn', properties: { privateLinkServiceId: kv.id, groupIds: [ 'vault' ] } }
    ]
  }
}

// 注意：Service Bus 的 Private Endpoint 只支持 Premium SKU。
// 用 Standard 建 PE 会直接报 PrivateEndpointInvalidSku，这是 SKU 边界而非配置错误。
// 因此这里不给 SB 建 PE，改用「公网入口 + 关闭本地认证（disableLocalAuth）+ 仅托管标识授权」
// 来保证安全：没有 AAD 令牌就拿不到队列，等同把攻击面收敛到身份层。

resource zoneSql 'Microsoft.Network/privateDnsZones@2020-06-01' = if (peMode) {
  name: dnsZones[0].zone
  location: 'global'
  tags: tags
}
resource zoneBlob 'Microsoft.Network/privateDnsZones@2020-06-01' = if (peMode) {
  name: dnsZones[1].zone
  location: 'global'
  tags: tags
}
resource zoneKv 'Microsoft.Network/privateDnsZones@2020-06-01' = if (peMode) {
  name: dnsZones[2].zone
  location: 'global'
  tags: tags
}
// SB 不建 PE，因此也不需要它的私有 DNS 区

resource linkSql 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (peMode) {
  parent: zoneSql
  name: 'link-sql-spoke'
  location: 'global'
  properties: { virtualNetwork: { id: vnetSpoke.id }, registrationEnabled: false }
}
resource linkBlob 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (peMode) {
  parent: zoneBlob
  name: 'link-blob-spoke'
  location: 'global'
  properties: { virtualNetwork: { id: vnetSpoke.id }, registrationEnabled: false }
}
resource linkKv 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (peMode) {
  parent: zoneKv
  name: 'link-kv-spoke'
  location: 'global'
  properties: { virtualNetwork: { id: vnetSpoke.id }, registrationEnabled: false }
}

resource dnsSql 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (peMode) {
  parent: peSql
  name: 'default'
  properties: { privateDnsZoneConfigs: [ { name: 'sql', properties: { privateDnsZoneId: zoneSql.id } } ] }
}
resource dnsBlob 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (peMode) {
  parent: peBlob
  name: 'default'
  properties: { privateDnsZoneConfigs: [ { name: 'blob', properties: { privateDnsZoneId: zoneBlob.id } } ] }
}
resource dnsKv 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (peMode) {
  parent: peKv
  name: 'default'
  properties: { privateDnsZoneConfigs: [ { name: 'kv', properties: { privateDnsZoneId: zoneKv.id } } ] }
}

// ─────────────────────────────────────────────────────────────
// 11. 身份与权限（托管标识，零密钥）
// ─────────────────────────────────────────────────────────────
var blobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
// Azure Service Bus Data Owner —— 注意别和 3ce6f3185d4d 混淆，那个 GUID 不存在
var sbDataOwner = '090c5cfd-751d-490a-894a-3ce6f1109419'

resource roleApiBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, appApi.id, blobDataContributor)
  scope: storage
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributor), principalId: appApi.identity.principalId, principalType: 'ServicePrincipal' }
}

resource roleApiSb 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableFunctions) {
  name: guid(sb.id, appApi.id, sbDataOwner)
  scope: sb
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataOwner), principalId: appApi.identity.principalId, principalType: 'ServicePrincipal' }
}

resource roleFuncBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableFunctions) {
  name: guid(storage.id, appFunc.id, blobDataContributor)
  scope: storage
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributor), principalId: appFunc.identity.principalId, principalType: 'ServicePrincipal' }
}

var queueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var tableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource roleFuncQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableFunctions) {
  name: guid(storage.id, appFunc.id, queueDataContributor)
  scope: storage
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', queueDataContributor), principalId: appFunc.identity.principalId, principalType: 'ServicePrincipal' }
}

resource roleFuncTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableFunctions) {
  name: guid(storage.id, appFunc.id, tableDataContributor)
  scope: storage
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', tableDataContributor), principalId: appFunc.identity.principalId, principalType: 'ServicePrincipal' }
}

resource roleFuncSb 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableFunctions) {
  name: guid(sb.id, appFunc.id, sbDataOwner)
  scope: sb
  properties: { roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataOwner), principalId: appFunc.identity.principalId, principalType: 'ServicePrincipal' }
}

// Key Vault 访问策略：三个应用只读密钥（一个资源内声明全部策略）
resource kvPolicies 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: kv
  name: 'add'
  properties: {
    accessPolicies: concat(
      [
        { tenantId: tenant().tenantId, objectId: appApi.identity.principalId, permissions: { secrets: [ 'get', 'list' ] } }
        { tenantId: tenant().tenantId, objectId: appWeb.identity.principalId, permissions: { secrets: [ 'get', 'list' ] } }
      ],
      enableFunctions ? [ { tenantId: tenant().tenantId, objectId: appFunc.identity.principalId, permissions: { secrets: [ 'get', 'list' ] } } ] : []
    )
  }
  dependsOn: [ appApi, appWeb ]
}

// Cosmos 数据面权限
var cosmosContributor = '00000000-0000-0000-0000-000000000002'
resource roleApiCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (enableCosmos) {
  parent: cosmos
  name: guid(cosmos.id, appApi.id, cosmosContributor)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosContributor}'
    principalId: appApi.identity.principalId
    scope: cosmos.id
  }
}

// ─────────────────────────────────────────────────────────────
// 12. 成本治理
// ─────────────────────────────────────────────────────────────
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (budgetAmount > 0) {
  name: 'budget-com-monthly'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    // 月度预算要求 startDate 落在当前月或之后；写死历史日期会报
    // 400 "Start date for monthly time grain should not be prior to current month"
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 50
        contactEmails: alertEmail == '' ? [] : [ alertEmail ]
      }
      actual80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        contactEmails: alertEmail == '' ? [] : [ alertEmail ]
      }
      actual100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        contactEmails: alertEmail == '' ? [] : [ alertEmail ]
      }
    }
  }
}

// ─────────────────────────────────────────────────────────────
// 输出
// ─────────────────────────────────────────────────────────────
output apiApp string = apiAppName
output webApp string = webAppName
output funcApp string = enableFunctions ? funcAppName : ''
output apiUrl string = 'https://${apiHost}'
output webUrl string = 'https://${webHost}'
output funcName string = enableFunctions ? appFunc.name : ''
output storageName string = storage.name
output kvName string = kv.name
output sqlFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sbNamespace string = enableFunctions ? sb.name : ''
output cosmosEndpoint string = enableCosmos ? cosmos.properties.documentEndpoint : ''
output logicAppTriggerUrl string = enableLogicApps ? listCallbackURL(resourceId('Microsoft.Logic/workflows/triggers', logicApp.name, 'manual'), '2019-05-01').value : ''
output managedIdentities object = {
  api: appApi.identity.principalId
  web: appWeb.identity.principalId
  func: enableFunctions ? appFunc.identity.principalId : ''
}
