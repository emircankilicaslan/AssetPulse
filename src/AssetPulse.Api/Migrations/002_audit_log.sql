-- Denetim kaydı (audit log): kim, ne zaman, hangi kaydı değiştirdi. Yalnızca eklenir; uygulama güncelleme/silme yapmaz.
CREATE TABLE dbo.AuditLog (
    Id         BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    OccurredAt DATETIME2(3)  NOT NULL CONSTRAINT DF_AuditLog_OccurredAt DEFAULT SYSUTCDATETIME(),
    Actor      NVARCHAR(60)  NOT NULL,   -- işlemi yapan kullanıcı adı (giriş denemesinde denenen ad)
    ActorRole  NVARCHAR(20)  NULL,
    Action     NVARCHAR(40)  NOT NULL,   -- ör. AssetCreated, AlertAcknowledged, LoginFailed
    EntityType NVARCHAR(20)  NULL,       -- Asset, Sensor, Rule, Alert, User
    EntityId   NVARCHAR(40)  NULL,
    Details    NVARCHAR(500) NULL        -- sır içermez (parola, API anahtarı asla yazılmaz)
);
CREATE INDEX IX_AuditLog_OccurredAt ON dbo.AuditLog (OccurredAt DESC, Id DESC);
CREATE INDEX IX_AuditLog_Actor ON dbo.AuditLog (Actor, OccurredAt DESC);
CREATE INDEX IX_AuditLog_Action ON dbo.AuditLog (Action, OccurredAt DESC);
