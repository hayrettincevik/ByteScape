namespace ByteScape;

/// <summary>Help texts shown by the quick help windows and the command line.</summary>
public static class HelpTexts
{
    public const string Filtering =
/*loc*/ @"Type conditions in the filter box and press Enter; separate them with ';'.
Names, tags and file classes: a file is shown when it matches any one of them.
Sizes, ages, attributes and folders: a file is shown when it matches all of them.
A '|' in front of a condition turns it around: everything that does NOT match.

NAMES           *.jpg   report?.docx   |*.tmp
                '*' stands for any text, '?' for exactly one character.

SIZE            >100mb   <16kb   >2.5gb
                Units: b, kb, mb, gb, tb (k, m, g and t work too).
                The size compared is the space a file takes on the disk. Put size,
                filesize or logicalsize before the sign to compare the file's own
                length instead: size>1mb (s>1mb for short). disksize and clustersize
                name the space on the disk: d<16k.

AGE             <6months   >2weeks   <10s
                Units: s, min, h, d, w, months, y, or the whole words.
                The age counts from the last change. Put created or accessed before
                the sign for those dates: created>1month, a>1y.
                <10s lists the files that are changing right now.

TAGS            :red   :yellow   :green   :blue   :all
                :tag:red+yellow   red or yellow        :tag:-red   tagged, but not red
                Short forms: r, y, g, b, and the tag keys 1 to 4.

ATTRIBUTES      :attr:hidden   :attr:h+s   hidden or system
                :attr:a-s   archive, but not system
                archive a, system s, readonly r, hidden h, compressed c, encrypted e,
                offline o, temporary t, notindexed ni, sparse sp, alternate streams ads

FILE CLASS      :class:Pictures   the classes and their extensions: Settings > Styles.
                Names with spaces need no quotes.

FOLDERS         \temp             only files that have a folder named temp in their path
                |\node_modules    everything outside the node_modules folders
                \proj*            wildcards work; a folder name cannot contain '\'
                The backslash marks a folder name, it does not mean the root of the drive.
                Folders that match are drawn in bold. Folder names filter more slowly.

KNOWN CACHES    :reclaimable      caches and temporary areas that can be emptied safely
                |:reclaimable     everything else       (:cache and :junk work too)
                Such folders carry a badge on the map.

CLOUD FILES     :cloud            files in OneDrive, Dropbox, iCloud and similar folders
                :freeable         local copies that ""Free up space"" can remove (the file
                                  stays in the cloud and on your other devices)
                :onlineonly       files kept only online: no space used on this disk
                                  (listed in exports, not drawn on the map)
                :alwayskept       files set to ""Always keep on this device""

PLAIN WORDS (English or Turkish)
                Write a sentence and press Enter; the box then shows the filter it was
                read as, and the status line explains it:
                  large files except videos
                  pictures smaller than 2 MB in Downloads folder
                  6 aydır açılmamış 100 MB üstü videolar
                  son 7 günde oluşturulan belgeler
                Understood: kinds of files (video, resim, music, belge, archive, iso,
                log...), extensions (psd files, .mkv), sizes (over 1 GB, 100 MB üstü,
                big, huge, small), ages (older than 1 year, 6 aydır açılmamış, bugün,
                this week), hidden, system and read-only files, tags, folders, caches.

EXAMPLES        *.jpg;*.png;>2mb          JPG or PNG pictures over 2 MB
                |*.exe;|*.dll;|\windows   no programs, nothing inside a windows folder
                :tag:r+y;>1gb             red or yellow tagged files over 1 GB
                :attr:h;|:attr:s          hidden files that are not system files";

    public const string Tagging =
/*loc*/ @"Colour tags mark files and folders you want to find again later: to keep, to check,
to move or to delete.

    Ctrl+1   red          Ctrl+2   yellow
    Ctrl+3   green        Ctrl+4   blue
    Ctrl+0   removes the tags in the current view (the zoomed folder and below)

A key tags, or untags, the block under the mouse pointer, otherwise the selected block.
While the filter box does not have the focus, the digits work without Ctrl too. The
right-click menu has the same commands.

A tag on a folder counts for everything inside it: filtering by that colour finds those
files as well. Only the folder itself shows the tag, so the map stays easy to read.

Filter by tag with :red, :yellow, :green, :blue or :all, with :tag:red+yellow for more
than one colour, and |:red for everything that is not red (see the filtering help).";

    public const string CommandLine =
/*loc*/ @"Commands are read from left to right. scan and load open a view; the commands
that follow belong to the view opened last.

help                          this text

scan ""<folder or drive>""      opens a view that scans the folder or drive;
                              join several with ';' (or repeat scan)
load ""<file>""                 opens a saved snapshot (.sns); join several with ';'
filter ""<filter>""             sets the view's filter (the filter box syntax)
export ""<name>"" ""<file>""      when the view is complete, writes it to <file> with the
                              export setting called <name> (spelled exactly as in the
                              Export window); a filter typed or a scan stopped by hand
                              switches the automatic exports off
compare ""<file.sns>""          compares the view with a snapshot when it is complete:
                              what grew, shrank, appeared or went, also on the map

apps                          opens ""Programs on disk"" when the view is complete: each
                              installed program with its folders, data and caches
appsreport ""<file>""           writes that list to a text file
games                         opens ""Games"": Steam, Epic Games and GOG games, their
                              folders, when they were last played, and the leftovers
                              of removed games
gamesreport ""<file>""          writes that list to a text file
dev                           opens ""Developer leftovers"": projects with the folders
                              their tools rebuild (node_modules, bin, obj, target, .venv)
                              and when each project last changed
devreport ""<file>""            writes that list to a text file
forecast                      opens the forecast of the view's drive: when it will be
                              full, where the new files went, what grew
cleanup                       opens ""Cleanup"": large and temporary files, old installers
                              and unused programs, each with whether it is safe to delete
cleanupreport ""<file>""        writes those lists to a text file (nothing is deleted)
duplicates                    opens ""Duplicate files"": the same content in several
                              places (same length, then SHA-256) and the space it takes
duplicatesreport ""<file>""     writes the duplicate groups: CSV for a .csv name, JSON
                              for .json, text otherwise (nothing is deleted)
comparefolder ""<folder>""      compares the view's folder with <folder>: only on one
                              side, different, the same
comparefolderreport ""<folder>"" ""<file>""
                              writes that comparison to <file> (CSV or text)

export ""CSV list (spreadsheet)"" ""<file>""    a list Excel opens
export ""JSON tree"" ""<file>""                 the whole tree as JSON
export ""HTML report"" ""<file>""               a single web page with charts

measure                       alone on the command line: records the free space of
                              every local drive for the forecast and exits without a
                              window (for Task Scheduler)
autoclose                     closes the program when every automatic export and report
                              is written; working in a window cancels it

The report commands wait for the view to be complete and work with autoclose.";
}
