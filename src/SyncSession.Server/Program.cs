// SyncSystem.Server is a class library — there is no entry point here.
// The deployable host is SyncSystem.Server.Host (src/SyncSystem.Server.Host/Program.cs).
// Consumers of the NuGet package call:
//   builder.Services.AddSyncSession(opts => { ... });
//   app.UseSyncSession();       // startup validation (Session 27d)
//   app.MapSyncEndpoints();    // route registration
