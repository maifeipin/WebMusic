import { useEffect, useState } from 'react';
import { usePlayer } from '../context/PlayerContext';
import { getFiles, getGroups, getSongsByIds } from '../services/api';
import { Play, Music, Folder, List, Grid, ChevronRight, ChevronDown, ArrowUp, ArrowDown, CheckSquare, Square, X, ListPlus, HardDrive } from 'lucide-react';
import DirectoryTree from '../components/DirectoryTree';
import AddToPlaylistModal from '../components/AddToPlaylistModal';
import { FileManager } from '../components/FileManager';

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

type SortField = 'title' | 'artist' | 'album' | 'genre' | 'filePath' | 'duration';
type SortDirection = 'asc' | 'desc';

interface ActiveFilter {
    field: 'artist' | 'album' | 'genre' | 'path';
    value: string;
}

export default function Library() {
    const [viewMode, setViewMode] = useState<'flat' | 'group' | 'directory'>('flat');
    const [groupBy, setGroupBy] = useState<string>('artist');

    const [songs, setSongs] = useState<Song[]>([]);
    const [page, setPage] = useState(1);
    const [search, setSearch] = useState('');
    const [total, setTotal] = useState(0);

    const [sortField, setSortField] = useState<SortField>('title');
    const [sortDirection, setSortDirection] = useState<SortDirection>('asc');

    const [selectedIds, setSelectedIds] = useState<number[]>([]);
    const [selectedPaths, setSelectedPaths] = useState<string[]>([]);
    const [selectedPathCounts, setSelectedPathCounts] = useState<Record<string, number>>({});
    const [resolvingSelection, setResolvingSelection] = useState(false);
    const [defaultPlaylistName, setDefaultPlaylistName] = useState('');
    const [resolvedIds, setResolvedIds] = useState<number[]>([]);

    const [activeFilter, setActiveFilter] = useState<ActiveFilter | null>(null);

    // Add to Playlist Modal
    const [showAddToPlaylist, setShowAddToPlaylist] = useState(false);

    const [groups, setGroups] = useState<GroupRow[]>([]);
    const [expandedGroups, setExpandedGroups] = useState<Record<string, Song[]>>({});
    const [expandedState, setExpandedState] = useState<Record<string, boolean>>({});

    const [showFileManager, setShowFileManager] = useState(false);

    const { playSong, playQueue } = usePlayer();

    useEffect(() => {
        setSelectedIds([]);
        setSelectedPaths([]);
        setSelectedPathCounts({});

        if (viewMode === 'flat') fetchSongs();
        else if (viewMode === 'group') {
            if (groupBy !== 'directory') fetchGroups();
        }
    }, [page, search, viewMode, groupBy, activeFilter]);

    const fetchSongs = async () => {
        const params: any = { page, search, pageSize: 50 };
        if (activeFilter) {
            if (activeFilter.field === 'path') {
                // For path filtering, use the path parameter with recursive
                params.path = activeFilter.value;
                params.recursive = false; // Only this directory
            } else {
                params.filterBy = activeFilter.field;
                params.filterValue = activeFilter.value;
            }
        }
        const res = await getFiles(params);
        setSongs(res.data.files);
        setTotal(res.data.total);
        setSelectedIds([]);
    };

    const fetchGroups = async () => {
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
        if (expandedGroups[groupKey]) return;
        const res = await getFiles({ page: 1, pageSize: 1000, filterBy: groupBy, filterValue: groupKey });
        setExpandedGroups(prev => ({ ...prev, [groupKey]: res.data.files }));
    };

    const sortedSongs = [...songs].sort((a, b) => {
        let aVal: any = a[sortField];
        let bVal: any = b[sortField];
        if (sortField === 'duration') {
            aVal = aVal || 0;
            bVal = bVal || 0;
        } else {
            aVal = (aVal || '').toString().toLowerCase();
            bVal = (bVal || '').toString().toLowerCase();
        }
        if (aVal < bVal) return sortDirection === 'asc' ? -1 : 1;
        if (aVal > bVal) return sortDirection === 'asc' ? 1 : -1;
        return 0;
    });

    const handleSort = (field: SortField) => {
        if (sortField === field) {
            setSortDirection(prev => prev === 'asc' ? 'desc' : 'asc');
        } else {
            setSortField(field);
            setSortDirection('asc');
        }
    };

    const handlePlay = (song: Song) => {
        if (viewMode === 'flat') {
            const index = sortedSongs.findIndex(s => s.id === song.id);
            if (index !== -1) {
                playQueue(sortedSongs, index);
            } else {
                playSong(song);
            }
        } else if (viewMode === 'group') {
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
        } else {
            playSong(song);
        }
    };

    const handlePlayGroup = async (groupKey: string) => {
        let songsToPlay: Song[] = [];
        if (expandedGroups[groupKey]) {
            songsToPlay = expandedGroups[groupKey];
        } else {
            const res = await getFiles({ page: 1, pageSize: 1000, filterBy: groupBy, filterValue: groupKey });
            songsToPlay = res.data.files;
            setExpandedGroups(prev => ({ ...prev, [groupKey]: songsToPlay }));
        }
        if (songsToPlay.length > 0) playQueue(songsToPlay, 0);
    };

    const handlePlayFolder = async (path: string) => {
        try {
            const res = await getFiles({ page: 1, pageSize: 2000, path: path, recursive: true });
            const songs = res.data.files;
            if (songs.length > 0) {
                playQueue(songs, 0);
            } else {
                alert("No songs found in this folder.");
            }
        } catch (e) { console.error(e); }
    };

    const toggleSelect = (id: number) => {
        setSelectedIds(prev =>
            prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]
        );
    };

    const toggleSelectPath = (path: string, count?: number) => {
        setSelectedPaths(prev => {
            const isSelected = prev.includes(path);
            if (isSelected) {
                if (count !== undefined) {
                    setSelectedPathCounts(c => {
                        const copy = { ...c };
                        delete copy[path];
                        return copy;
                    });
                }
                return prev.filter(x => x !== path);
            } else {
                if (count !== undefined) {
                    setSelectedPathCounts(c => ({ ...c, [path]: count }));
                }
                return [...prev, path];
            }
        });
    };

    const toggleSelectAll = () => {
        if (selectedIds.length === sortedSongs.length) {
            setSelectedIds([]);
        } else {
            setSelectedIds(sortedSongs.map(s => s.id));
        }
    };

    const resolveSelection = async (): Promise<number[]> => {
        let allIds = [...selectedIds];
        if (selectedPaths.length > 0) {
            setResolvingSelection(true);
            try {
                const promises = selectedPaths.map(path => getFiles({ path, recursive: true, pageSize: 20000 }));
                const results = await Promise.all(promises);
                results.forEach(res => {
                    if (res.data && res.data.files) {
                        const ids = res.data.files.map((f: any) => f.id);
                        allIds = [...allIds, ...ids];
                    }
                });
            } catch (e) {
                console.error("Failed to resolve selection", e);
            } finally {
                setResolvingSelection(false);
            }
        }
        return Array.from(new Set(allIds));
    };

    const handlePlaySelected = async () => {
        // Fast path for flat view
        if (viewMode === 'flat' && selectedPaths.length === 0) {
            const selected = sortedSongs.filter(s => selectedIds.includes(s.id));
            if (selected.length > 0) playQueue(selected, 0);
            return;
        }

        // Resolve for mixed selection
        const ids = await resolveSelection();
        if (ids.length > 0) {
            try {
                const res = await getSongsByIds(ids);
                playQueue(res.data, 0);
            } catch (e) { console.error(e); }
        }
    };

    const handleAddToPlaylistClick = async () => {
        setResolvingSelection(true);
        const ids = await resolveSelection();
        setResolvedIds(ids);

        let name = '';
        if (selectedPaths.length === 1 && selectedIds.length === 0) {
            const parts = selectedPaths[0].split(/[/\\]/);
            name = parts[parts.length - 1] || parts[parts.length - 2] || 'New Playlist';
        }
        setDefaultPlaylistName(name);

        setResolvingSelection(false);
        setShowAddToPlaylist(true);
    };

    // Extract directory path from full file path
    const extractDirectoryPath = (filePath: string): string => {
        if (!filePath) return '';
        // Normalize slashes
        const normalized = filePath.replace(/\\/g, '/');
        // Find last slash
        const lastSlash = normalized.lastIndexOf('/');
        if (lastSlash > 0) {
            return normalized.substring(0, lastSlash);
        }
        return normalized;
    };

    const handleCellClick = (field: 'artist' | 'album' | 'genre' | 'path', value: string, filePath?: string) => {
        if (!value && field !== 'path') return;

        let filterValue = value;

        // For path, extract directory from the full file path
        if (field === 'path') {
            if (!filePath) return;
            filterValue = extractDirectoryPath(filePath);
            if (!filterValue) return;
        }

        setActiveFilter({ field, value: filterValue });
        setPage(1);
    };

    const clearFilter = () => {
        setActiveFilter(null);
        setPage(1);
    };

    const formatTime = (seconds: number) =>
        `${Math.floor(seconds / 60)}:${(Math.floor(seconds % 60)).toString().padStart(2, '0')}`;

    const SortHeader = ({ field, label, className = '' }: { field: SortField; label: string; className?: string }) => (
        <th
            className={`px-4 py-3 cursor-pointer hover:bg-gray-800 select-none transition ${className}`}
            onClick={() => handleSort(field)}
        >
            <div className="flex items-center gap-1">
                <span>{label}</span>
                {sortField === field && (
                    sortDirection === 'asc' ? <ArrowUp size={14} /> : <ArrowDown size={14} />
                )}
            </div>
        </th>
    );

    const selectionCount = selectedIds.length + Object.values(selectedPathCounts).reduce((a, b) => a + (b || 0), 0);
    const hasSelection = selectedIds.length > 0 || selectedPaths.length > 0;

    return (
        <div className="flex flex-col h-full bg-black text-white">
            <div className="p-8 flex-1 overflow-auto mb-24">
                {/* Header Controls */}
                <div className="flex flex-col md:flex-row justify-between items-start md:items-center mb-6 gap-4">
                    <div className="flex items-center gap-4 flex-wrap">
                        <h1 className="text-3xl font-bold">Library</h1>
                        <div className="flex bg-gray-800 rounded p-1">
                            <button onClick={() => setViewMode('flat')} className={`p-2 rounded ${viewMode === 'flat' ? 'bg-gray-700 text-white' : 'text-gray-400'}`} title="List View"><List size={18} /></button>
                            <button onClick={() => setViewMode('group')} className={`p-2 rounded ${viewMode === 'group' ? 'bg-gray-700 text-white' : 'text-gray-400'}`} title="Group View"><Grid size={18} /></button>
                            <button onClick={() => setViewMode('directory')} className={`p-2 rounded ${viewMode === 'directory' ? 'bg-gray-700 text-white' : 'text-gray-400'}`} title="Directory View"><Folder size={18} /></button>
                            <div className="w-px h-6 bg-gray-700 mx-1"></div>
                            <button onClick={() => setShowFileManager(true)} className="p-2 rounded text-gray-400 hover:text-white hover:bg-gray-700" title="NAS Manager"><HardDrive size={18} /></button>
                        </div>

                        {/* Action Buttons for Selected Items */}
                        {hasSelection && (
                            <div className="flex items-center gap-2">
                                <button
                                    onClick={handlePlaySelected}
                                    disabled={resolvingSelection}
                                    className="flex items-center gap-2 px-4 py-2 bg-blue-600 hover:bg-blue-500 disabled:opacity-50 text-white rounded-lg font-medium transition shadow-lg shadow-blue-900/30"
                                >
                                    {resolvingSelection ? (
                                        <span className="animate-pulse">Loading...</span>
                                    ) : (
                                        <>
                                            <Play size={18} fill="currentColor" />
                                            Play ({selectionCount})
                                        </>
                                    )}
                                </button>
                                <button
                                    onClick={handleAddToPlaylistClick}
                                    disabled={resolvingSelection}
                                    className="flex items-center gap-2 px-4 py-2 bg-green-600 hover:bg-green-500 disabled:opacity-50 text-white rounded-lg font-medium transition shadow-lg shadow-green-900/30"
                                >
                                    <ListPlus size={18} />
                                    Add to Playlist ({selectionCount})
                                </button>
                            </div>
                        )}
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

                {activeFilter && (
                    <div className="mb-4 flex items-center gap-2">
                        <span className="text-gray-400 text-sm">Filtering by:</span>
                        <div className="flex items-center gap-2 bg-blue-600/20 border border-blue-500/50 text-blue-400 px-3 py-1 rounded-full text-sm">
                            <span className="font-medium capitalize">{activeFilter.field}:</span>
                            <span className="max-w-[300px] truncate">{activeFilter.value}</span>
                            <button onClick={clearFilter} className="hover:text-white ml-1"><X size={14} /></button>
                        </div>
                    </div>
                )}

                {viewMode === 'flat' && (
                    <div className="overflow-x-auto">
                        <table className="w-full text-left text-sm text-gray-400">
                            <thead className="bg-gray-900 uppercase font-medium">
                                <tr>
                                    <th className="px-4 py-3 w-10">
                                        <button
                                            onClick={toggleSelectAll}
                                            className={`p-1 rounded transition ${selectedIds.length === sortedSongs.length && sortedSongs.length > 0 ? 'text-blue-500' : 'text-gray-500 hover:text-gray-300'}`}
                                        >
                                            {selectedIds.length === sortedSongs.length && sortedSongs.length > 0 ? <CheckSquare size={18} /> : <Square size={18} />}
                                        </button>
                                    </th>
                                    <th className="px-4 py-3 w-12">#</th>
                                    <SortHeader field="title" label="Title" />
                                    <SortHeader field="artist" label="Artist" />
                                    <SortHeader field="album" label="Album" />
                                    <SortHeader field="genre" label="Genre" />
                                    <SortHeader field="filePath" label="Path" className="w-48" />
                                    <SortHeader field="duration" label="Time" className="w-20" />
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-gray-800">
                                {sortedSongs.map((song, i) => {
                                    const isSelected = selectedIds.includes(song.id);
                                    const directoryPath = extractDirectoryPath(song.filePath || '');
                                    return (
                                        <tr
                                            key={song.id}
                                            className={`hover:bg-gray-800/50 group cursor-pointer transition ${isSelected ? 'bg-blue-600/10' : ''}`}
                                            onDoubleClick={() => handlePlay(song)}
                                        >
                                            <td className="px-4 py-3">
                                                <button
                                                    onClick={(e) => { e.stopPropagation(); toggleSelect(song.id); }}
                                                    className={`p-1 rounded transition ${isSelected ? 'text-blue-500' : 'text-gray-600 hover:text-gray-400'}`}
                                                >
                                                    {isSelected ? <CheckSquare size={16} /> : <Square size={16} />}
                                                </button>
                                            </td>
                                            <td className="px-4 py-3 text-center">
                                                <span className="group-hover:hidden">{(page - 1) * 50 + i + 1}</span>
                                                <button onClick={() => handlePlay(song)} className="hidden group-hover:block text-blue-400"><Play size={16} fill="currentColor" /></button>
                                            </td>
                                            <td className="px-4 py-3 font-medium text-white">{song.title}</td>
                                            <td
                                                className="px-4 py-3 hover:text-blue-400 hover:underline cursor-pointer transition"
                                                onClick={(e) => { e.stopPropagation(); handleCellClick('artist', song.artist); }}
                                                title={`Filter by artist: ${song.artist}`}
                                            >
                                                {song.artist}
                                            </td>
                                            <td
                                                className="px-4 py-3 hover:text-blue-400 hover:underline cursor-pointer transition"
                                                onClick={(e) => { e.stopPropagation(); handleCellClick('album', song.album); }}
                                                title={`Filter by album: ${song.album}`}
                                            >
                                                {song.album}
                                            </td>
                                            <td
                                                className="px-4 py-3 hover:text-blue-400 hover:underline cursor-pointer transition"
                                                onClick={(e) => { e.stopPropagation(); handleCellClick('genre', song.genre); }}
                                                title={`Filter by genre: ${song.genre}`}
                                            >
                                                {song.genre}
                                            </td>
                                            <td
                                                className="px-4 py-3 hover:text-blue-400 cursor-pointer transition max-w-[200px]"
                                                onClick={(e) => { e.stopPropagation(); handleCellClick('path', directoryPath, song.filePath); }}
                                                title={`Filter by path: ${directoryPath}`}
                                            >
                                                <div className="truncate text-xs font-mono text-gray-500 hover:text-blue-400">{directoryPath}</div>
                                            </td>
                                            <td className="px-4 py-3">{song.duration && formatTime(song.duration)}</td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                        <div className="flex justify-center mt-6 gap-2">
                            <button disabled={page === 1} onClick={() => setPage(page - 1)} className="px-3 py-1 bg-gray-800 rounded disabled:opacity-50">Prev</button>
                            <span className="px-3 py-1 text-gray-400">Page {page} • {total} songs</span>
                            <button disabled={page * 50 >= total} onClick={() => setPage(page + 1)} className="px-3 py-1 bg-gray-800 rounded disabled:opacity-50">Next</button>
                        </div>
                    </div>
                )}

                {viewMode === 'group' && groupBy === 'directory' && (
                    <div className="bg-gray-900/30 rounded-lg p-4 min-h-[500px] border border-gray-800">
                        <DirectoryTree
                            onPlayFile={(id, title, artist, album) => {
                                handlePlay({ id, title, artist, album, genre: '', year: 0, duration: 0, filePath: '' });
                            }}
                            onPlayFolder={(path) => handlePlayFolder(path)}
                            selectedPaths={selectedPaths}
                            selectedFileIds={selectedIds}
                            onTogglePath={toggleSelectPath}
                            onToggleFile={toggleSelect}
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
                                            onClick={(e) => { e.stopPropagation(); handlePlayGroup(group.key); }}
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
                                handlePlay({ id, title, artist, album, genre: '', year: 0, duration: 0, filePath: '' });
                            }}
                            onPlayFolder={(path) => handlePlayFolder(path)}
                            selectedPaths={selectedPaths}
                            selectedFileIds={selectedIds}
                            onTogglePath={toggleSelectPath}
                            onToggleFile={toggleSelect}
                        />
                    </div>
                )}
            </div>

            {/* Add to Playlist Modal */}
            <AddToPlaylistModal
                isOpen={showAddToPlaylist}
                onClose={() => setShowAddToPlaylist(false)}
                songIds={resolvedIds}
                defaultNewPlaylistName={defaultPlaylistName}
            />

            {/* File Manager Modal */}
            {showFileManager && (
                <FileManager onClose={() => setShowFileManager(false)} />
            )}
        </div>
    );

}
