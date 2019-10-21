using System;
using System.Net.WebSockets;

namespace server.models
{
  public class User
  {
    public string Username { get; set; }
    public WebSocket WebSocket { get; set; }
  }
}