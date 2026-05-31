using System;
using FluentAssertions;
using SyncSession.Core.Models;
using Xunit;

namespace SyncSession.UnitTests.Client;

/// <summary>
/// Unit tests for progress reporting functionality
/// Tests focus on SyncProgress calculations and status messages
/// End-to-end progress tests are in integration tests
/// </summary>
public class ClientSyncEngineProgressTests
{
    [Fact]
    public void SyncProgress_StatusMessage_ConnectingPhase()
    {
        // Arrange
        var progress = new SyncProgress { Phase = SyncPhase.Connecting };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Connecting to server...");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_PushBeginPhase()
    {
        // Arrange
        var progress = new SyncProgress { Phase = SyncPhase.PushBegin };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Starting push session...");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_PushTablePhase()
    {
        // Arrange
        var progress = new SyncProgress 
        { 
            Phase = SyncPhase.PushTable,
            CurrentTable = "Customers",
            RecordsProcessed = 50,
            TotalRecords = 100
        };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Pushing Customers (50/100)");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_PushCompletePhase()
    {
        // Arrange
        var progress = new SyncProgress { Phase = SyncPhase.PushComplete };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Finalizing push...");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_PullBeginPhase()
    {
        // Arrange
        var progress = new SyncProgress { Phase = SyncPhase.PullBegin };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Starting pull session...");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_PullTablePhase()
    {
        // Arrange
        var progress = new SyncProgress 
        { 
            Phase = SyncPhase.PullTable,
            CurrentTable = "Orders",
            RecordsProcessed = 250,
            TotalRecords = 1000
        };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Pulling Orders (250/1,000)");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_PullCompletePhase()
    {
        // Arrange
        var progress = new SyncProgress { Phase = SyncPhase.PullComplete };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Finalizing pull...");
    }
    
    [Fact]
    public void SyncProgress_StatusMessage_CompletePhase()
    {
        // Arrange
        var progress = new SyncProgress { Phase = SyncPhase.Complete };
        
        // Act & Assert
        progress.StatusMessage.Should().Be("Sync complete!");
    }
    
    [Fact]
    public void SyncProgress_TablePercent_NoRecords_ReturnsZero()
    {
        // Arrange
        var progress = new SyncProgress
        {
            RecordsProcessed = 0,
            TotalRecords = 0
        };
        
        // Act
        var percent = progress.TablePercent;
        
        // Assert
        percent.Should().Be(0);
    }
    
    [Fact]
    public void SyncProgress_TablePercent_HalfComplete_Returns50()
    {
        // Arrange
        var progress = new SyncProgress
        {
            RecordsProcessed = 500,
            TotalRecords = 1000
        };
        
        // Act
        var percent = progress.TablePercent;
        
        // Assert
        percent.Should().BeApproximately(50.0, 0.01);
    }
    
    [Fact]
    public void SyncProgress_TablePercent_FullyComplete_Returns100()
    {
        // Arrange
        var progress = new SyncProgress
        {
            RecordsProcessed = 1000,
            TotalRecords = 1000
        };
        
        // Act
        var percent = progress.TablePercent;
        
        // Assert
        percent.Should().BeApproximately(100.0, 0.01);
    }
    
    [Fact]
    public void SyncProgress_OverallPercent_NoTables_ReturnsZero()
    {
        // Arrange
        var progress = new SyncProgress
        {
            TablesCompleted = 0,
            TotalTables = 0
        };
        
        // Act
        var percent = progress.OverallPercent;
        
        // Assert
        percent.Should().Be(0);
    }
    
    [Fact]
    public void SyncProgress_OverallPercent_FirstTableHalfComplete()
    {
        // Arrange - 2 push tables + 2 pull tables = 4 total
        // Currently on first table (index 0), 50% through
        var progress = new SyncProgress
        {
            TotalTables = 4,
            TablesCompleted = 0,
            RecordsProcessed = 50,
            TotalRecords = 100
        };
        
        // Act
        var percent = progress.OverallPercent;
        
        // Assert
        // (0 + 0.5) / 4 * 100 = 12.5%
        percent.Should().BeApproximately(12.5, 0.01);
    }
    
    [Fact]
    public void SyncProgress_OverallPercent_SecondTableHalfComplete()
    {
        // Arrange - 2 push tables + 2 pull tables = 4 total
        // Currently on second table (index 1), 50% through
        var progress = new SyncProgress
        {
            TotalTables = 4,
            TablesCompleted = 1,
            RecordsProcessed = 500,
            TotalRecords = 1000
        };
        
        // Act
        var percent = progress.OverallPercent;
        
        // Assert
        // (1 + 0.5) / 4 * 100 = 37.5%
        percent.Should().BeApproximately(37.5, 0.01);
    }
    
    [Fact]
    public void SyncProgress_OverallPercent_ThirdTableComplete()
    {
        // Arrange - 2 push tables + 2 pull tables = 4 total
        // Currently on third table (index 2), 100% complete
        var progress = new SyncProgress
        {
            TotalTables = 4,
            TablesCompleted = 2,
            RecordsProcessed = 1000,
            TotalRecords = 1000
        };
        
        // Act
        var percent = progress.OverallPercent;
        
        // Assert
        // (2 + 1.0) / 4 * 100 = 75%
        percent.Should().BeApproximately(75.0, 0.01);
    }
    
    [Fact]
    public void SyncProgress_OverallPercent_AllTablesComplete()
    {
        // Arrange - Completed all 4 tables
        var progress = new SyncProgress
        {
            TotalTables = 4,
            TablesCompleted = 4,  // Past the last table
            RecordsProcessed = 0,
            TotalRecords = 0
        };
        
        // Act
        var percent = progress.OverallPercent;
        
        // Assert
        // (4 + 0) / 4 * 100 = 100%
        percent.Should().BeApproximately(100.0, 0.01);
    }
    
    [Fact]
    public void SyncProgress_OverallPercent_ComplexScenario()
    {
        // Arrange - 6 total tables (3 push + 3 pull)
        // Currently on 4th table (index 3), 25% through
        var progress = new SyncProgress
        {
            TotalTables = 6,
            TablesCompleted = 3,
            RecordsProcessed = 250,
            TotalRecords = 1000
        };
        
        // Act
        var percent = progress.OverallPercent;
        
        // Assert
        // (3 + 0.25) / 6 * 100 = 54.1667%
        percent.Should().BeApproximately(54.17, 0.01);
    }
}
