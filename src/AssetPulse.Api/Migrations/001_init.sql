-- AssetPulse ilk şema. DbUp bu betiği yalnızca bir kez çalıştırır; uygulanmış betik değiştirilmez,
-- yeni değişiklikler yeni numaralı bir betikle eklenir.

CREATE TABLE dbo.Users (
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Username     NVARCHAR(60)  NOT NULL CONSTRAINT UQ_Users_Username UNIQUE,
    PasswordHash NVARCHAR(200) NOT NULL,
    Role         NVARCHAR(20)  NOT NULL CONSTRAINT CK_Users_Role CHECK (Role IN (N'Admin', N'Operator')),
    CreatedAt    DATETIME2(3)  NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Assets (
    Id         INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
    Code       NVARCHAR(40)  NOT NULL CONSTRAINT UQ_Assets_Code UNIQUE,
    Name       NVARCHAR(120) NOT NULL,
    Category   TINYINT       NOT NULL,
    Location   NVARCHAR(120) NULL,
    IsActive   BIT           NOT NULL CONSTRAINT DF_Assets_IsActive DEFAULT 1,
    ApiKeyHash CHAR(64)      NOT NULL CONSTRAINT UQ_Assets_ApiKeyHash UNIQUE,
    CreatedAt  DATETIME2(3)  NOT NULL CONSTRAINT DF_Assets_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Sensors (
    Id      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sensors PRIMARY KEY,
    AssetId INT           NOT NULL CONSTRAINT FK_Sensors_Assets REFERENCES dbo.Assets(Id),
    Code    NVARCHAR(40)  NOT NULL,
    Name    NVARCHAR(120) NOT NULL,
    Unit    NVARCHAR(16)  NOT NULL,
    CONSTRAINT UQ_Sensors_Asset_Code UNIQUE (AssetId, Code)
);

-- Doğal anahtar (SensorId, MeasuredAt): hem tekrar eden ölçümü engeller (idempotent alım)
-- hem de zaman aralığı sorguları için kümelenmiş dizin görevi görür.
CREATE TABLE dbo.Readings (
    SensorId   INT           NOT NULL CONSTRAINT FK_Readings_Sensors REFERENCES dbo.Sensors(Id),
    MeasuredAt DATETIME2(3)  NOT NULL,
    Value      DECIMAL(18,4) NOT NULL,
    CONSTRAINT PK_Readings PRIMARY KEY CLUSTERED (SensorId, MeasuredAt)
);

CREATE TABLE dbo.AlertRules (
    Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertRules PRIMARY KEY,
    SensorId         INT           NOT NULL CONSTRAINT FK_AlertRules_Sensors REFERENCES dbo.Sensors(Id),
    Kind             TINYINT       NOT NULL CONSTRAINT CK_AlertRules_Kind CHECK (Kind IN (0, 1)),
    Threshold        DECIMAL(18,4) NOT NULL,
    Hysteresis       DECIMAL(18,4) NOT NULL CONSTRAINT DF_AlertRules_Hysteresis DEFAULT 0 CONSTRAINT CK_AlertRules_Hysteresis CHECK (Hysteresis >= 0),
    ConsecutiveCount INT           NOT NULL CONSTRAINT DF_AlertRules_Consecutive DEFAULT 1 CONSTRAINT CK_AlertRules_Consecutive CHECK (ConsecutiveCount BETWEEN 1 AND 1000),
    Severity         TINYINT       NOT NULL CONSTRAINT CK_AlertRules_Severity CHECK (Severity IN (0, 1, 2)),
    IsActive         BIT           NOT NULL CONSTRAINT DF_AlertRules_IsActive DEFAULT 1,
    ViolationStreak  INT           NOT NULL CONSTRAINT DF_AlertRules_Streak DEFAULT 0
);
CREATE INDEX IX_AlertRules_Sensor ON dbo.AlertRules (SensorId) WHERE IsActive = 1;

CREATE TABLE dbo.Alerts (
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Alerts PRIMARY KEY,
    RuleId         INT           NOT NULL CONSTRAINT FK_Alerts_Rules REFERENCES dbo.AlertRules(Id),
    AssetId        INT           NOT NULL CONSTRAINT FK_Alerts_Assets REFERENCES dbo.Assets(Id),
    SensorId       INT           NOT NULL CONSTRAINT FK_Alerts_Sensors REFERENCES dbo.Sensors(Id),
    Severity       TINYINT       NOT NULL,
    Status         TINYINT       NOT NULL CONSTRAINT CK_Alerts_Status CHECK (Status IN (0, 1, 2)), -- 0 Aktif, 1 Üstlenildi, 2 Kapandı
    OpenedAt       DATETIME2(3)  NOT NULL,
    OpenValue      DECIMAL(18,4) NOT NULL,
    AcknowledgedBy NVARCHAR(60)  NULL,
    AcknowledgedAt DATETIME2(3)  NULL,
    ClearedAt      DATETIME2(3)  NULL,
    Note           NVARCHAR(500) NULL
);
CREATE INDEX IX_Alerts_Status_OpenedAt ON dbo.Alerts (Status, OpenedAt DESC);
CREATE INDEX IX_Alerts_Asset ON dbo.Alerts (AssetId, Status);
-- Bir kural için aynı anda en fazla bir açık (aktif/üstlenilmiş) alarm olabilir.
CREATE UNIQUE INDEX UX_Alerts_OpenPerRule ON dbo.Alerts (RuleId) WHERE Status IN (0, 1);
