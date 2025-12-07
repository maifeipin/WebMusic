import { useEffect, useState } from 'react';
import { usePlayer } from '../context/PlayerContext';
import { getFiles, getGroups } from '../services/api';
import { Play, Music, Folder, List, Grid, ChevronRight, ChevronDown } from 'lucide-react';
import DirectoryTree from '../components/DirectoryTree';

interface Song {
    id: number;
    title: string;
    artist: string;
    album: string;
    genre: string;
    year: number;
    duration: number;
    filePath?: string;
}

interface GroupRow {
    key: string;
    count: number;
}

export default function Library() {
    // Modes: 'flat', 'group', 'directory'
    const [viewMode, setViewMode] = useState<'flat' | 'group' | 'directory'>('flat');
    const [groupBy, setGroupBy] = useState<string>('artist'); // Default group

    // Flat Data
    const [songs, setSongs] = useState<Song[]>([]);
    const [page, setPage] = useState(1);
    const [search, setSearch] = useState('');
    const [total, setTotal] = useState(0);

    // Grouping Data
    const [groups, setGroups] = useState<GroupRow[]>([]);
    const [expandedGroups, setExpandedGroups] = useState<Record<string, Song[]>>({}); // Cache fetched songs per group
    const [expandedState, setExpandedState] = useState<Record<string, boolean>>({});

    // Player State (Global)
    const { playSong, playQueue } = usePlayer();

    useEffect(() => {
        if (viewMode === 'flat') fetchSongs();
        else if (viewMode === 'group') {
            if (groupBy !== 'directory') fetchGroups();
        }
        // directory mode handles its own fetch via component
    }, [page, search, viewMode, groupBy]);

    const fetchSongs = async () => {
        const res = await getFiles({ page, search, pageSize: 50 });
        setSongs(res.data.files);
        setTotal(res.data.total);
    };

    const fetchGroups = async () => {
        // Fetch groups (e.g. list of Artists)
        try {
            const res = await getGroups(groupBy);
            setGroups(res.data);
            setExpandedState({});
            setExpandedGroups({});
        } catch (e) {
            console.error(e);
        }
    };

    const fetchGroupContent = async (groupKey: string) => {
        // Fetch songs for this group
        if (expandedGroups[groupKey]) return; // Already loaded

        const res = await getFiles({ page: 1, pageSize: 1000, filterBy: groupBy, filterValue: groupKey });
        setExpandedGroups(prev => ({ ...prev, [groupKey]: res.data.files }));
    };



    const handlePlay = (song: Song) => {
        // If in a list (songs or expanded group), play that list starting from this song
        if (viewMode === 'flat') {
            const index = songs.findIndex(s => s.id === song.id);
            if (index !== -1) {
                playQueue(songs, index);
            } else {
                playSong(song);
            }
        } else if (viewMode === 'group') {
            // Find which group this song belongs to
            // Optimization: Pass context from UI loop
            // For now, simple fallback: search all loaded groups
            let foundGroup: Song[] | null = null;
            let foundIndex = -1;
            Object.values(expandedGroups).forEach(groupSongs => {
                const idx = groupSongs.findIndex(s => s.id === song.id);
                if (idx !== -1) {
                    foundGroup = groupSongs;
                    foundIndex = idx;
                }
            });

            if (foundGroup) {
                playQueue(foundGroup, foundIndex);
            } else {
                playSong(song);
            }
        } else if (viewMode === 'directory') {
            // In directory mode, we play the single file. 
            // Future improvement: DirectoryTree could pass siblings to create a queue.
            playSong(song);
        } else {
            playSong(song);
        }
    };

    const handlePlayGroup = async (groupKey: string) => {
        // Prepare songs
        let songsToPlay: Song[] = [];
        if (expandedGroups[groupKey]) {
            songsToPlay = expandedGroups[groupKey];
        } else {
            // Fetch if not present
            const res = await getFiles({ page: 1, pageSize: 1000, filterBy: groupBy, filterValue: groupKey });
            songsToPlay = res.data.files;
            // Optionally cache it
            setExpandedGroups(prev => ({ ...prev, [groupKey]: songsToPlay }));
        }
        if (songsToPlay.length > 0) playQueue(songsToPlay, 0);
    };

    const handlePlayFolder = async (path: string) => {
        // Recursive Play: Fetch all files in this folder OR subfolders
        try {
            // Use GetFiles with recursive=true
            // PageSize large to get all (for now limit 2000)
            const res = await getFiles({ page: 1, pageSize: 2000, path: path, recursive: true });
            const songs = res.data.files;

            if (songs.length > 0) {
                playQueue(songs, 0);
            } else {
                alert("No songs found in this folder.");
            }
        } catch (e) { console.error(e); }
    };



    const formatTime = (seconds: number) =>
        `${Math.floor(seconds / 60)}:${(Math.floor(seconds % 60)).toString().padStart(2, '0')}`;

    return (
        <div className="flex flex-col h-full bg-black text-white">
            <div className="p-8 flex-1 overflow-auto mb-24">
                {/* Header Controls */}
                <div className="flex flex-col md:flex-row justify-between items-start md:items-center mb-6 gap-4">
                    <div className="flex items-center gap-4">
                        <h1 className="text-3xl font-bold">Library</h1>
                        <div className="flex bg-gray-800 rounded p-1">
                            <button onClick={() => setViewMode('flat')} className={`p-2 rounded ${viewMode === 'flat' ? 'bg-gray-700 text-white' : 'text-gray-400'}`} title="List View"><List size={18} /></button>
                            <button onClick={() => setViewMode('group')} className={`p-2 rounded ${viewMode === 'group' ? 'bg-gray-700 text-white' : 'text-gray-400'}`} title="Group View"><Grid size={18} /></button>
                            <button onClick={() => setViewMode('directory')} className={`p-2 rounded ${viewMode === 'directory' ? 'bg-gray-700 text-white' : 'text-gray-400'}`} title="Directory View"><Folder size={18} /></button>
                        </div>
                    </div>

                    <div className="flex gap-4 items-center">
                        {viewMode === 'group' && (
                            <select value={groupBy} onChange={e => setGroupBy(e.target.value)} className="bg-gray-800 border border-gray-700 rounded px-3 py-2 text-sm focus:outline-none">
                                <option value="artist">Group by Artist</option>
                                <option value="album">Group by Album</option>
                                <option value="genre">Group by Genre</option>
                                <option value="year">Group by Year</option>
                                <option value="directory">Group by Directory</option>
                            </select>
                        )}
                        <input
                            placeholder="Search..."
                            value={search}
                            onChange={e => setSearch(e.target.value)}
                            className="bg-gray-800 rounded-full px-4 py-2 w-64 focus:outline-none focus:ring-1 focus:ring-blue-500 text-sm"
                        />
                    </div>
                </div>

                {/* VIEW Modes */}

                {viewMode === 'flat' && (
                    <div className="overflow-x-auto">
                        <table className="w-full text-left text-sm text-gray-400">
                            <thead className="bg-gray-900 uppercase font-medium">
                                <tr>
                                    <th className="px-4 py-3 w-12">#</th>
                                    <th className="px-4 py-3">Title</th>
                                    <th className="px-4 py-3">Artist</th>
                                    <th className="px-4 py-3">Album</th>
                                    <th className="px-4 py-3">Genre</th>
                                    <th className="px-4 py-3 w-48">Path</th>
                                    <th className="px-4 py-3 w-20">Time</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-800">
                                {songs.map((song, i) => (
                                    <tr key={song.id} className="hover:bg-gray-800/50 group cursor-pointer transition" onDoubleClick={() => handlePlay(song)}>
                                        <td className="px-4 py-3 text-center">
                                            <span className="group-hover:hidden">{(page - 1) * 50 + i + 1}</span>
                                            <button onClick={() => handlePlay(song)} className="hidden group-hover:block text-blue-400"><Play size={16} fill="currentColor" /></button>
                                        </td>
                                        <td className="px-4 py-3 font-medium text-white">{song.title}</td>
                                        <td className="px-4 py-3">{song.artist}</td>
                                        <td className="px-4 py-3">{song.album}</td>
                                        <td className="px-4 py-3">{song.genre}</td>
                                        <td className="px-4 py-3 max-w-[200px]" title={song.filePath}>
                                            <div className="truncate text-xs font-mono text-gray-500">{song.filePath}</div>
                                        </td>
                                        <td className="px-4 py-3">{song.duration && formatTime(song.duration)}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                        {/* Pagination */}
                        <div className="flex justify-center mt-6 gap-2">
                            <button disabled={page === 1} onClick={() => setPage(page - 1)} className="px-3 py-1 bg-gray-800 rounded disabled:opacity-50">Prev</button>
                            <span className="px-3 py-1 text-gray-400">Page {page}</span>
                            <button disabled={page * 50 >= total} onClick={() => setPage(page + 1)} className="px-3 py-1 bg-gray-800 rounded disabled:opacity-50">Next</button>
                        </div>
                    </div>
                )}

                {viewMode === 'group' && groupBy === 'directory' && (
                    <div className="bg-gray-900/30 rounded-lg p-4 min-h-[500px] border border-gray-800">
                        <DirectoryTree
                            onPlayFile={(id, title, artist, album) => {
                                handlePlay({
                                    id, title, artist, album,
                                    genre: '', year: 0, duration: 0, filePath: ''
                                });
                            }}
                            onPlayFolder={(path) => handlePlayFolder(path)}
                        />
                    </div>
                )}

                {viewMode === 'group' && groupBy !== 'directory' && (
                    <div className="space-y-2">
                        {groups.map((group) => {
                            const isExpanded = !!expandedState[group.key];
                            return (
                                <div key={group.key} className="bg-gray-900 rounded-lg overflow-hidden border border-gray-800">
                                    <div
                                        className="p-4 flex items-center justify-between cursor-pointer hover:bg-gray-800 transition"
                                        onClick={() => {
                                            const nextState = !isExpanded;
                                            setExpandedState(prev => ({ ...prev, [group.key]: nextState }));
                                            if (nextState) fetchGroupContent(group.key);
                                        }}
                                    >
                                        <div className="flex items-center gap-3">
                                            {isExpanded ? <ChevronDown size={20} /> : <ChevronRight size={20} />}
                                            <span className="font-bold text-lg text-white">{group.key || 'Unknown'}</span>
                                            <span className="text-sm text-gray-500 bg-gray-950 px-2 py-0.5 rounded-full">{group.count} songs</span>
                                        </div>
                                        <button
                                            onClick={(e) => {
                                                e.stopPropagation();
                                                handlePlayGroup(group.key);
                                            }}
                                            className="p-2 hover:bg-gray-700 rounded-full text-blue-400"
                                            title="Play Group"
                                        >
                                            <Play size={20} fill="currentColor" />
                                        </button>
                                    </div>

                                    {isExpanded && (
                                        <div className="bg-black/50 border-t border-gray-800">
                                            {expandedGroups[group.key] ? (
                                                <table className="w-full text-left text-sm text-gray-400">
                                                    <tbody className="divide-y divide-gray-800/50">
                                                        {expandedGroups[group.key].map(song => (
                                                            <tr key={song.id} className="hover:bg-gray-800/30 group cursor-pointer" onDoubleClick={() => handlePlay(song)}>
                                                                <td className="px-8 py-2 w-12"><Music size={14} className="group-hover:text-blue-400" /></td>
                                                                <td className="px-4 py-2 text-white">{song.title}</td>
                                                                <td className="px-4 py-2">{song.album}</td>
                                                                <td className="px-4 py-2 text-right">{formatTime(song.duration)}</td>
                                                            </tr>
                                                        ))}
                                                    </tbody>
                                                </table>
                                            ) : (
                                                <div className="p-4 text-center text-gray-500">Loading...</div>
                                            )}
                                        </div>
                                    )}
                                </div>
                            );
                        })}
                    </div>
                )}

                {viewMode === 'directory' && (
                    <div className="bg-gray-900/30 rounded-lg p-4 min-h-[500px] border border-gray-800">
                        <DirectoryTree
                            onPlayFile={(id, title, artist, album) => {
                                handlePlay({
                                    id, title, artist, album,
                                    genre: '', year: 0, duration: 0, filePath: ''
                                });
                            }}
                            onPlayFolder={(path) => handlePlayFolder(path)}
                        />
                    </div>
                )}
            </div>

            {/* Persistent Player (Same as before) */}
        </div>
    );
}

