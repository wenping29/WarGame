using HexFront.Api.Hubs;
using HexFront.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<RoomService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Raw WebSocket session protocol used by web/js/ws.js and the React client.
app.Map("/ws", async (HttpContext context, RoomService rooms) => await rooms.HandleAsync(context));

// SignalR hub (alternative transport for future clients).
app.MapHub<RoomHub>("/hubs/room");

app.MapControllers();

app.Run();
