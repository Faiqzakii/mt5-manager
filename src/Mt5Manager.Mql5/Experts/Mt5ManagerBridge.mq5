#property copyright "MT5 Manager"
#property version "1.00"
#property strict

#define PROTOCOL_VERSION 1
#define REFRESH_SECONDS 3

input string InpFileNamePrefix = "Mt5Manager/runtime-";

datetime nextUpdate;

int OnInit()
{
   EventSetTimer(1);
   nextUpdate = 0;
   WriteSnapshot();
   return(INIT_SUCCEEDED);
}

void OnDeinit(const int reason)
{
   EventKillTimer();
}

void OnTimer()
{
   if(TimeGMT() < nextUpdate)
      return;
   WriteSnapshot();
}

void WriteSnapshot()
{
   string path = UpperPath(CanonicalDataPath(TerminalInfoString(TERMINAL_DATA_PATH)));
   string hash = DataPathHash(path);
   string finalName = InpFileNamePrefix + hash + ".json";
   string tempName = finalName + ".tmp";
   string backupName = finalName + ".bak";

   // A previous update can be interrupted after rotating the final snapshot.
   // Restore that stranded backup before starting the next rotation.
   if(!FileIsExist(finalName, FILE_COMMON) && FileIsExist(backupName, FILE_COMMON))
   {
      if(!FileMove(backupName, FILE_COMMON, finalName, FILE_COMMON))
         return;
   }

   string json = "{" +
      "\"protocolVersion\":" + IntegerToString(PROTOCOL_VERSION) + "," +
      "\"timestamp\":\"" + IsoUtc(TimeGMT()) + "\"," +
      "\"dataPath\":" + Quote(TerminalInfoString(TERMINAL_DATA_PATH)) + "," +
      "\"login\":" + IntegerToString(AccountInfoInteger(ACCOUNT_LOGIN)) + "," +
      "\"accountName\":" + Quote(AccountInfoString(ACCOUNT_NAME)) + "," +
      "\"server\":" + Quote(AccountInfoString(ACCOUNT_SERVER)) + "," +
      "\"company\":" + Quote(AccountInfoString(ACCOUNT_COMPANY)) + "," +
      "\"tradeMode\":" + Quote(TradeModeName((ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE))) + "," +
      "\"connected\":" + BoolJson((bool)TerminalInfoInteger(TERMINAL_CONNECTED)) + "," +
      "\"globalAlgoTrading\":" + Quote(AlgoJson((bool)TerminalInfoInteger(TERMINAL_TRADE_ALLOWED))) + "," +
      "\"eaTradingAllowed\":" + BoolJson((bool)MQLInfoInteger(MQL_TRADE_ALLOWED)) + "," +
      "\"accountTradingAllowed\":" + BoolJson((bool)AccountInfoInteger(ACCOUNT_TRADE_ALLOWED)) + "," +
      "\"accountExpertAllowed\":" + BoolJson((bool)AccountInfoInteger(ACCOUNT_TRADE_EXPERT)) +
      "}";

   int handle = FileOpen(tempName, FILE_WRITE|FILE_TXT|FILE_UNICODE|FILE_COMMON);
   if(handle == INVALID_HANDLE)
      return;

   FileWriteString(handle, json);
   FileClose(handle);

   // MQL5 has no replace-existing atomic rename. Preserve the last good snapshot
   // as a backup until the new file is in place, then remove the backup.
   bool hadSnapshot = FileIsExist(finalName, FILE_COMMON);
   if(hadSnapshot)
   {
      FileDelete(backupName, FILE_COMMON);
      if(!FileMove(finalName, FILE_COMMON, backupName, FILE_COMMON))
      {
         FileDelete(tempName, FILE_COMMON);
         return;
      }
   }

   if(FileMove(tempName, FILE_COMMON, finalName, FILE_COMMON))
   {
      if(hadSnapshot)
         FileDelete(backupName, FILE_COMMON);
   }
   else
   {
      FileDelete(tempName, FILE_COMMON);
      if(hadSnapshot)
         FileMove(backupName, FILE_COMMON, finalName, FILE_COMMON);
   }

   nextUpdate = TimeGMT() + REFRESH_SECONDS;
}

string DataPathHash(string path)
{
   uchar data[];
   uchar key[];
   uchar hash[];
   int copied = StringToCharArray(path, data, 0, -1, CP_UTF8);
   ArrayResize(data, copied > 0 ? copied - 1 : 0);
   CryptEncode(CRYPT_HASH_SHA256, data, key, hash);

   string hex = "";
   for(int i = 0; i < ArraySize(hash); i++)
      hex += StringFormat("%02x", hash[i]);
   return hex;
}

string CanonicalDataPath(string path)
{
   string trimmed = path;
   while(StringLen(trimmed) > 0)
   {
      ushort last = StringGetCharacter(trimmed, StringLen(trimmed) - 1);
      if(last != '\\' && last != '/')
         break;
      trimmed = StringSubstr(trimmed, 0, StringLen(trimmed) - 1);
   }
   return trimmed;
}

string UpperPath(string value)
{
   string upper = value;
   StringToUpper(upper);
   return upper;
}

string TradeModeName(ENUM_ACCOUNT_TRADE_MODE mode)
{
   if(mode == ACCOUNT_TRADE_MODE_DEMO)
      return "Demo";
   if(mode == ACCOUNT_TRADE_MODE_CONTEST)
      return "Contest";
   if(mode == ACCOUNT_TRADE_MODE_REAL)
      return "Real";
   return "Unknown";
}

string AlgoJson(bool enabled)
{
   return enabled ? "Enabled" : "Disabled";
}

string BoolJson(bool value)
{
   return value ? "true" : "false";
}

string Quote(string value)
{
   StringReplace(value, "\\", "\\\\");
   StringReplace(value, "\"", "\\\"");
   StringReplace(value, "\r", "\\r");
   StringReplace(value, "\n", "\\n");
   StringReplace(value, "\t", "\\t");
   return "\"" + value + "\"";
}

string IsoUtc(datetime value)
{
   MqlDateTime utc;
   TimeToStruct(value, utc);
   return StringFormat("%04d-%02d-%02dT%02d:%02d:%02dZ", utc.year, utc.mon, utc.day, utc.hour, utc.min, utc.sec);
}
