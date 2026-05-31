-- Auto-set IsDirty on INSERT/UPDATE
CREATE TRIGGER IF NOT EXISTS trg_customers_insert
AFTER INSERT ON Customers
FOR EACH ROW
BEGIN
    UPDATE Customers 
    SET IsDirty = 1, 
        ModifiedAtUtc = datetime('now')
    WHERE Id = NEW.Id;
END;

CREATE TRIGGER IF NOT EXISTS trg_customers_update
AFTER UPDATE ON Customers
FOR EACH ROW
WHEN NEW.IsDirty = 0
BEGIN
    UPDATE Customers 
    SET IsDirty = 1,
        ModifiedAtUtc = datetime('now')
    WHERE Id = NEW.Id;
END;
