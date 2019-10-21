using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace server.Middleware
{
  public class ChatWebSocketMiddleware
  {
    private static ConcurrentDictionary<string, WebSocket> _sockets = new ConcurrentDictionary<string, WebSocket>();

    private readonly RequestDelegate _next;

    public ChatWebSocketMiddleware(RequestDelegate next)
    {
      _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
      if (!context.WebSockets.IsWebSocketRequest)
      {
        await _next.Invoke(context);
        return;
      }

      //verifica se foi enviado o nome do usuário
      var user = context.Request.Query["user"];
      if (string.IsNullOrEmpty(user))
      {
        context.Response.StatusCode = 400;
        return;
      }

      user = $"@{user}";

      //verifica se já existe o nome do usuário;
      var userExits = _sockets.FirstOrDefault(p => p.Key == user).Value;
      if (userExits != null)
      {
        context.Response.StatusCode = 401;
        return;
      }

      CancellationToken ct = context.RequestAborted;
      WebSocket currentSocket = await context.WebSockets.AcceptWebSocketAsync();

      _sockets.TryAdd(user, currentSocket);

      await SendMessageToAllAsync(null, $"{user} entrou na sala");

      while (!ct.IsCancellationRequested)
      {
        var response = await ReceiveStringAsync(currentSocket, ct);

        if (string.IsNullOrEmpty(response))
        {
          if (currentSocket.State != WebSocketState.Open)
          {
            break;
          }

          continue;
        }

        if (response.ToLower().StartsWith("/exit"))
        {
          break;
        }

        if (response.ToLower().StartsWith("/p"))
        {
          await SendMessagePrivateAsync(currentSocket, response, ct);
          continue;
        }

        await SendMessageToAllAsync(currentSocket, response, ct);
      }

      await SendMessageToAllAsync(null, $"{user} saiu da sala");

      WebSocket dummy;
      _sockets.TryRemove(user, out dummy);

      await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "CloseReceived", ct);
      currentSocket.Dispose();
    }

    private static async Task SendMessageToAllAsync(WebSocket currentSocket, string message, CancellationToken ct = default(CancellationToken))
    {
      var userSource = _sockets.FirstOrDefault(p => p.Value == currentSocket).Key;

      if (userSource != null)
      {
        if (message.StartsWith("@"))
        {
          var parts = message.Split(' ').ToList();

          var userDestination = _sockets.FirstOrDefault(p => p.Key == parts[0]).Key;

          if (!string.IsNullOrEmpty(userDestination))
          {
            parts.RemoveAt(0);
            message = $"{userSource} disse para {userDestination}: { String.Join(" ", parts)}";
          }
          else
          {
            await SendStringAsync(currentSocket, $"** Não foi possível entregar a mensagem! {parts[0]} não encontrado!", ct);
            return;
          }
        }
        else
        {
          message = $"{userSource} disse: {message}";
        }
      }

      foreach (var socket in _sockets)
      {
        if (socket.Value == currentSocket)
          continue;

        if (socket.Value.State == WebSocketState.Open)
          await SendStringAsync(socket.Value, message, ct);
      }
    }

    private static async Task SendMessagePrivateAsync(WebSocket currentSocket, string message, CancellationToken ct = default(CancellationToken))
    {
      var userSource = _sockets.FirstOrDefault(p => p.Value == currentSocket).Key;

      var parts = message.Split(' ').ToList();

      var userDestination = _sockets.FirstOrDefault(p => p.Key == parts[1]).Value;

      if (userDestination != null)
      {
        parts.RemoveRange(0, 2);
        message = $"{userSource} disse em privado: {String.Join(" ", parts)}";
        await SendStringAsync(userDestination, message, ct);
        return;
      }

      await SendStringAsync(currentSocket, $"** Não foi possível entregar a mensagem! Verifique o nome do usuário.", ct);
    }

    private static Task SendStringAsync(WebSocket socket, string message, CancellationToken ct = default(CancellationToken))
    {
      var buffer = Encoding.UTF8.GetBytes(message);
      var segment = new ArraySegment<byte>(buffer);

      return socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string> ReceiveStringAsync(WebSocket socket, CancellationToken ct = default(CancellationToken))
    {
      var buffer = new ArraySegment<byte>(new byte[8192]);
      using (var ms = new MemoryStream())
      {
        WebSocketReceiveResult result;
        do
        {
          ct.ThrowIfCancellationRequested();

          result = await socket.ReceiveAsync(buffer, ct);
          ms.Write(buffer.Array, buffer.Offset, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        if (result.MessageType != WebSocketMessageType.Text)
        {
          return null;
        }

        // Encoding UTF8: https://tools.ietf.org/html/rfc6455#section-5.6
        using (var reader = new StreamReader(ms, Encoding.UTF8))
        {
          return await reader.ReadToEndAsync();
        }
      }
    }
  }
}