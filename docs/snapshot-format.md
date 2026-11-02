# Snapshot format (.sns)

A snapshot stores a scanned tree so that it can be opened, compared and exported later without the disk. It is written by
`ByteScape.Core/Core/Snapshot/SnsFormat.cs` ("Export → Snapshot", the `export` command line option with a snapshot
setting) and read back by the same class (double-click a `.sns` file, `load "<file>"`).

All numbers are little-endian. The file is one node record: the root folder, with every other node nested inside it.

## Node record

```
byte    0x02                 node start
byte    type                 0x03 = folder (the root too), 0x02 = file
int32   length               length in bytes of the name text that follows
byte[]  name                 base64 of the UTF-8 name, wrapped with CRLF every 72 characters;
                             the root's name is its full path ending in '\'
int64   size                 logical size in bytes
-- file:
int32   slack                size on disk minus logical size (0 when smaller)
int32   attributes           Win32 file attributes
-- folder:
int64   disk                 size on disk of the folder plus the slack of its direct files
                             (informational: a reader recomputes sizes from the children)
-- both:
int64   created              FILETIME (UTC); 0 for folders and unknown times
int64   accessed             FILETIME (UTC)
int64   modified             FILETIME (UTC)
byte    tag                  colour tags: red = 1, yellow = 2, green = 4, blue = 8
byte    reserved             0
-- folder only:
...     children             node records, biggest first
byte    0x01, byte 0x00      node end
```

A file's record ends with its own `0x01 0x00` end marker right after the reserved byte.

## Reading rules

- Folders are opened and closed on an explicit stack, never by recursion. A file that nests folders more than
  `FsNode.MaxDepth` (256) levels deep is refused, so a crafted snapshot cannot exhaust the thread's stack.
- Names longer than 1 MiB, a missing root folder or an unexpected byte end the read with a clear error.
- Paths in a snapshot are only shown: a snapshot view never opens, deletes or queries files, and network paths in it are
  never looked up (see [security-review.md](security-review.md)).
