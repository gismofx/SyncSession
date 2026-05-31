using System.CommandLine;
using SyncSession.Tools.Commands;

var root = new RootCommand("SyncSystem migration tools — assess, clone, migrate, validate");

root.AddCommand(AssessCommand.Build());
root.AddCommand(CloneCommand.Build());
root.AddCommand(MigrateCommand.Build());
root.AddCommand(ValidateCommand.Build());

return await root.InvokeAsync(args);
