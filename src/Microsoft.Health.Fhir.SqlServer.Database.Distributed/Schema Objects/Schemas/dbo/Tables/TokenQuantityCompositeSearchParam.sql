﻿CREATE TABLE dbo.TokenQuantityCompositeSearchParam
(
    ResourceTypeId smallint NOT NULL,
    TransactionId               bigint              NOT NULL,
    ShardletId                  tinyint             NOT NULL,
    Sequence                    smallint            NOT NULL,
    SearchParamId smallint NOT NULL,
    SystemId1 int NULL,
    Code1 varchar(128) COLLATE Latin1_General_100_CS_AS NOT NULL,
    SystemId2 int NULL,
    QuantityCodeId2 int NULL,
    SingleValue2 decimal(18,6) NULL,
    LowValue2 decimal(18,6) NULL,
    HighValue2 decimal(18,6) NULL,
    IsHistory bit NOT NULL,
)
GO
--ALTER TABLE dbo.TokenQuantityCompositeSearchParam SET ( LOCK_ESCALATION = AUTO )
GO
CREATE CLUSTERED INDEX IXC_TokenQuantityCompositeSearchParam
ON dbo.TokenQuantityCompositeSearchParam
(
    ResourceTypeId,
    TransactionId, ShardletId, Sequence,
    SearchParamId
)
WITH (DATA_COMPRESSION = PAGE)
ON PartitionScheme_ResourceTypeId(ResourceTypeId)
GO
CREATE INDEX IX_TokenQuantityCompositeSearchParam_SearchParamId_Code1_QuantityCodeId2_SingleValue2
ON dbo.TokenQuantityCompositeSearchParam
(
    ResourceTypeId,
    SearchParamId,
    Code1,
    SingleValue2,
    TransactionId, ShardletId, Sequence
)
INCLUDE
(
    QuantityCodeId2,
    SystemId1,
    SystemId2
)
WHERE IsHistory = 0 AND SingleValue2 IS NOT NULL
WITH (DATA_COMPRESSION = PAGE)
ON PartitionScheme_ResourceTypeId(ResourceTypeId)
GO
CREATE INDEX IX_TokenQuantityCompositeSearchParam_SearchParamId_Code1_QuantityCodeId2_LowValue2_HighValue2
ON dbo.TokenQuantityCompositeSearchParam
(
    ResourceTypeId,
    SearchParamId,
    Code1,
    LowValue2,
    HighValue2,
    TransactionId, ShardletId, Sequence
)
INCLUDE
(
    QuantityCodeId2,
    SystemId1,
    SystemId2
)
WHERE IsHistory = 0 AND LowValue2 IS NOT NULL
WITH (DATA_COMPRESSION = PAGE)
ON PartitionScheme_ResourceTypeId(ResourceTypeId)
GO
CREATE INDEX IX_TokenQuantityCompositeSearchParam_SearchParamId_Code1_QuantityCodeId2_HighValue2_LowValue2
ON dbo.TokenQuantityCompositeSearchParam
(
    ResourceTypeId,
    SearchParamId,
    Code1,
    HighValue2,
    LowValue2,
    TransactionId, ShardletId, Sequence
)
INCLUDE
(
    QuantityCodeId2,
    SystemId1,
    SystemId2
)
WHERE IsHistory = 0 AND LowValue2 IS NOT NULL
WITH (DATA_COMPRESSION = PAGE)
ON PartitionScheme_ResourceTypeId(ResourceTypeId)
GO