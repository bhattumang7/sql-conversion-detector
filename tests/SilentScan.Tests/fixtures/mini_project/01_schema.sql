CREATE TABLE dbo.Users
(
    UserId      INT             NOT NULL PRIMARY KEY,
    DisplayName VARCHAR(40)     COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    Region      VARCHAR(20)     COLLATE Latin1_General_CI_AS NOT NULL,
    CreatedAt   DATETIME        NOT NULL,
    Age         INT             NULL,
    Email       VARCHAR(100)    COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    Phone       VARCHAR(20)     COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    AccountCode VARCHAR(15)     COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
);
GO
CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
GO
CREATE INDEX IX_Users_Email ON dbo.Users(Email);
GO
CREATE INDEX IX_Users_Phone ON dbo.Users(Phone);
GO
CREATE INDEX IX_Users_AccountCode ON dbo.Users(AccountCode);
GO

CREATE TABLE dbo.Orders
(
    OrderId   INT           NOT NULL PRIMARY KEY,
    OrderCode VARCHAR(20)   COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    UserId    INT           NOT NULL
);
GO
CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
GO
