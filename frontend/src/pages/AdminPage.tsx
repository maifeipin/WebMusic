import { useEffect, useState, useRef } from 'react';
import { getUsers, adminResetPassword, createUser, deleteUser, getFavoritesEnrichmentPreview, startFavoritesEnrichment, retryFailedFavoritesEnrichment, getEnrichmentStatus, getDeletedFiles, restoreMedia, batchRestoreMedia } from '../services/api';
import { useAuth } from '../context/AuthContext';
import { Shield, Key, User, Plus, Trash2, Sparkles, LoaderCircle, RotateCcw, ArchiveRestore, Search, X, CheckSquare, Square, Undo2 } from 'lucide-react';
import { Pagination } from '../components/Pagination';

export default function AdminPage() {
    const { username } = useAuth();
    const isAdmin = username === 'admin';
    const [users, setUsers] = useState<{ id: number, username: string }[]>([]);
    const [loading, setLoading] = useState(false);
    const [enrichmentPreview, setEnrichmentPreview] = useState<number | null>(null);
    const [enrichmentStatus, setEnrichmentStatus] = useState<{ batchId: string; total: number; processed: number; success?: number; updated?: number; matchedWithoutAssets?: number; unmatched?: number; failed: number; status: string } | null>(null);
    const [enrichmentStarting, setEnrichmentStarting] = useState(false);
    const [showDeleted, setShowDeleted] = useState(false);
    const [deletedSongs, setDeletedSongs] = useState<any[]>([]);
    const [loadingDeleted, setLoadingDeleted] = useState(false);
    const [deletedPage, setDeletedPage] = useState(1);
    const [deletedPageSize, setDeletedPageSize] = useState(20);
    const [deletedTotal, setDeletedTotal] = useState(0);
    const [deletedSearch, setDeletedSearch] = useState('');
    const [searchInput, setSearchInput] = useState('');
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const [restoringId, setRestoringId] = useState<number | null>(null);
    const [batchRestoring, setBatchRestoring] = useState(false);
    const searchDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);


    useEffect(() => {
        if (isAdmin) {
            setLoading(true);
            getUsers()
                .then(setUsers)
                .catch(err => {
                    console.error(err);
                    // Don't alert on load, just log
                })
                .finally(() => setLoading(false));
            getFavoritesEnrichmentPreview()
                .then(result => setEnrichmentPreview(result.total))
                .catch(err => console.error(err));
        }
    }, [isAdmin]);

    useEffect(() => {
        if (!enrichmentStatus?.batchId || enrichmentStatus.status === 'Completed') return;
        const timer = window.setInterval(() => {
            getEnrichmentStatus(enrichmentStatus.batchId)
                .then(setEnrichmentStatus)
                .catch(err => console.error(err));
        }, 3000);
        return () => window.clearInterval(timer);
    }, [enrichmentStatus?.batchId, enrichmentStatus?.status]);

    const handleFavoritesEnrichment = async () => {
        if (!confirm(`Match and fill missing covers/lyrics for ${enrichmentPreview ?? 'eligible'} favorite songs? Existing metadata will not be overwritten.`)) return;
        setEnrichmentStarting(true);
        try {
            const result = await startFavoritesEnrichment();
            if (!result.batchId) {
                setEnrichmentPreview(0);
                alert(result.message);
                return;
            }
            setEnrichmentStatus({ batchId: result.batchId, total: result.total, processed: 0, success: 0, failed: 0, status: 'Queued' });
            setEnrichmentPreview(Math.max(0, (enrichmentPreview ?? result.total) - result.total));
        } catch (e: any) {
            alert('Failed to start enrichment: ' + (e.response?.data || e.message));
        } finally {
            setEnrichmentStarting(false);
        }
    };

    const handleRetryFailures = async () => {
        setEnrichmentStarting(true);
        try {
            const result = await retryFailedFavoritesEnrichment();
            if (!result.batchId) {
                alert(result.message);
                return;
            }
            setEnrichmentStatus({ batchId: result.batchId, total: result.total, processed: 0, success: 0, failed: 0, status: 'Queued' });
        } catch (e: any) {
            alert('Failed to retry enrichment: ' + (e.response?.data || e.message));
        } finally {
            setEnrichmentStarting(false);
        }
    };

    const loadDeleted = async (
        targetPage = deletedPage,
        targetPageSize = deletedPageSize,
        targetSearch = deletedSearch
    ) => {
        setLoadingDeleted(true);
        try {
            const res = await getDeletedFiles({
                page: targetPage,
                pageSize: targetPageSize,
                search: targetSearch.trim() ? targetSearch.trim() : undefined,
                sortBy: 'deletedat',
                sortDirection: 'desc'
            });
            setDeletedSongs(res.data?.files || []);
            setDeletedTotal(res.data?.total || 0);
            setSelectedIds(new Set());
        } catch (e) {
            console.error(e);
        } finally {
            setLoadingDeleted(false);
        }
    };

    const handleToggleShowDeleted = (checked: boolean) => {
        setShowDeleted(checked);
        if (checked) {
            loadDeleted(deletedPage, deletedPageSize, deletedSearch);
        }
    };

    const handleSearchInputChange = (val: string) => {
        setSearchInput(val);
        if (searchDebounceRef.current) {
            clearTimeout(searchDebounceRef.current);
        }
        searchDebounceRef.current = setTimeout(() => {
            setDeletedSearch(val);
            setDeletedPage(1);
            loadDeleted(1, deletedPageSize, val);
        }, 350);
    };

    const handleSearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (searchDebounceRef.current) {
            clearTimeout(searchDebounceRef.current);
        }
        setDeletedSearch(searchInput);
        setDeletedPage(1);
        loadDeleted(1, deletedPageSize, searchInput);
    };

    const handleClearSearch = () => {
        if (searchDebounceRef.current) {
            clearTimeout(searchDebounceRef.current);
        }
        setSearchInput('');
        setDeletedSearch('');
        setDeletedPage(1);
        loadDeleted(1, deletedPageSize, '');
    };

    const handlePageChange = (newPage: number) => {
        setDeletedPage(newPage);
        loadDeleted(newPage, deletedPageSize, deletedSearch);
    };

    const handlePageSizeChange = (newSize: number) => {
        setDeletedPageSize(newSize);
        setDeletedPage(1);
        loadDeleted(1, newSize, deletedSearch);
    };

    const allOnPageSelected = deletedSongs.length > 0 && deletedSongs.every(s => selectedIds.has(s.id));

    const toggleSelectAll = () => {
        if (allOnPageSelected) {
            const newSet = new Set(selectedIds);
            deletedSongs.forEach(s => newSet.delete(s.id));
            setSelectedIds(newSet);
        } else {
            const newSet = new Set(selectedIds);
            deletedSongs.forEach(s => newSet.add(s.id));
            setSelectedIds(newSet);
        }
    };

    const toggleSelectSong = (id: number) => {
        const newSet = new Set(selectedIds);
        if (newSet.has(id)) {
            newSet.delete(id);
        } else {
            newSet.add(id);
        }
        setSelectedIds(newSet);
    };

    const handleRestoreSong = async (id: number, title: string) => {
        if (!confirm(`确认恢复曲目《${title}》？恢复后将在普通曲库（Library）中重新显示。`)) return;
        setRestoringId(id);
        try {
            await restoreMedia(id);
            const nextSelected = new Set(selectedIds);
            nextSelected.delete(id);
            setSelectedIds(nextSelected);

            const targetPage = (deletedSongs.length === 1 && deletedPage > 1) ? deletedPage - 1 : deletedPage;
            setDeletedPage(targetPage);
            await loadDeleted(targetPage, deletedPageSize, deletedSearch);
        } catch (e: any) {
            alert('恢复失败: ' + (e.response?.data?.message || e.message));
        } finally {
            setRestoringId(null);
        }
    };

    const handleBatchRestore = async () => {
        const count = selectedIds.size;
        if (count === 0) return;
        if (!confirm(`确认批量恢复已选中的 ${count} 首曲目？恢复后将在普通曲库（Library）中重新显示。`)) return;

        setBatchRestoring(true);
        try {
            await batchRestoreMedia(Array.from(selectedIds));
            setSelectedIds(new Set());
            const targetPage = (deletedSongs.length <= count && deletedPage > 1) ? deletedPage - 1 : deletedPage;
            setDeletedPage(targetPage);
            await loadDeleted(targetPage, deletedPageSize, deletedSearch);
        } catch (e: any) {
            alert('批量恢复失败: ' + (e.response?.data?.message || e.message));
        } finally {
            setBatchRestoring(false);
        }
    };

    const handleReset = async (userId: number, userName: string) => {
        const newPass = prompt(`Enter new password for user '${userName}':`);
        if (!newPass) return;

        try {
            await adminResetPassword(userId, newPass);
            alert("Password reset successfully.");
        } catch (e: any) {
            alert("Failed: " + (e.response?.data || e.message));
        }
    };

    const handleCreate = async () => {
        const username = prompt("Enter new username:");
        if (!username) return;
        const password = prompt("Enter password for new user:");
        if (!password) return;

        try {
            await createUser({ username, password });
            const updated = await getUsers();
            setUsers(updated);
        } catch (e: any) {
            alert("Failed to create user: " + (e.response?.data || e.message));
        }
    };

    const handleDelete = async (id: number, username: string) => {
        if (!confirm(`Are you sure you want to delete user '${username}'? This will also delete their playlists and history.`)) return;

        try {
            await deleteUser(id);
            setUsers(users.filter(u => u.id !== id));
        } catch (e: any) {
            alert("Failed to delete user: " + (e.response?.data || e.message));
        }
    };

    if (!isAdmin) {
        return (
            <div className="h-full flex items-center justify-center text-gray-500">
                <div className="text-center">
                    <Shield size={48} className="mx-auto mb-4 opacity-20" />
                    <h2 className="text-xl font-bold text-gray-400">Restricted Access</h2>
                    <p className="text-sm mt-2">Only administrators can view this page.</p>
                </div>
            </div>
        );
    }

    return (
        <div className="h-full flex flex-col p-8 space-y-6 overflow-y-auto">
            <header>
                <h1 className="text-3xl font-bold text-white flex items-center gap-2">
                    <Shield className="text-red-500" /> Admin Console
                </h1>
                <p className="text-gray-400 mt-1">Manage users and system security</p>
            </header>

            <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6 max-w-2xl">
                <div className="flex items-center justify-between mb-4">
                    <h2 className="text-xl font-bold text-white flex items-center gap-2">
                        <User size={20} /> User Management
                    </h2>
                    <button
                        onClick={handleCreate}
                        className="flex items-center gap-1 px-3 py-1.5 bg-blue-600 hover:bg-blue-500 text-white rounded-lg text-sm font-bold transition"
                    >
                        <Plus size={16} /> Add User
                    </button>
                </div>

                {loading ? (
                    <div className="text-gray-500 text-sm">Loading users...</div>
                ) : (
                    <div className="space-y-2">
                        {users.map(u => (
                            <div key={u.id} className="flex items-center justify-between p-3 bg-gray-800 rounded-lg hover:bg-gray-750 transition">
                                <div className="flex items-center gap-3">
                                    <div className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-bold ${u.username === 'admin' ? 'bg-red-500/20 text-red-500' : 'bg-gray-700 text-gray-300'}`}>
                                        {u.username[0].toUpperCase()}
                                    </div>
                                    <div>
                                        <div className="font-bold text-white flex items-center gap-2">
                                            {u.username}
                                            {u.username === 'admin' && <span className="text-[10px] bg-red-500/10 text-red-500 px-1.5 py-0.5 rounded border border-red-500/20">ADMIN</span>}
                                        </div>
                                        <div className="text-xs text-gray-500">ID: {u.id}</div>
                                    </div>
                                </div>
                                <div className="flex items-center gap-2">
                                    <button
                                        onClick={() => handleReset(u.id, u.username)}
                                        className="px-3 py-1.5 bg-gray-700 hover:bg-yellow-600 hover:text-white text-gray-300 rounded-lg text-xs font-bold transition flex items-center gap-1"
                                        title="Reset Password"
                                    >
                                        <Key size={14} /> Reset
                                    </button>
                                    {u.id !== 1 && (
                                        <button
                                            onClick={() => handleDelete(u.id, u.username)}
                                            className="p-1.5 bg-gray-700 hover:bg-red-600 hover:text-white text-gray-300 rounded-lg transition"
                                            title="Delete User"
                                        >
                                            <Trash2 size={14} />
                                        </button>
                                    )}
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </div>

            <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6 max-w-2xl">
                <div className="flex items-start justify-between gap-4">
                    <div>
                        <h2 className="text-xl font-bold text-white flex items-center gap-2">
                            <Sparkles size={20} className="text-violet-400" /> Library enrichment
                        </h2>
                        <p className="text-sm text-gray-400 mt-2">
                            Matches favorite tracks against MusicBrainz, then fills only missing cover art and lyrics. Existing fields are never overwritten.
                        </p>
                        {enrichmentStatus ? (
                            <p className="text-sm text-violet-300 mt-3">
                                {enrichmentStatus.status}: {enrichmentStatus.processed}/{enrichmentStatus.total} processed · {enrichmentStatus.updated ?? enrichmentStatus.success ?? 0} updated{enrichmentStatus.matchedWithoutAssets ? ` · ${enrichmentStatus.matchedWithoutAssets} no assets` : ''}{enrichmentStatus.unmatched ? ` · ${enrichmentStatus.unmatched} unmatched` : ''} · {enrichmentStatus.failed} failed
                            </p>
                        ) : (
                            <p className="text-sm text-gray-500 mt-3">
                                {enrichmentPreview === null ? 'Checking eligible favorites…' : `${enrichmentPreview} favorite songs need a cover or lyrics.`}
                            </p>
                        )}
                    </div>
                    <button
                        onClick={handleFavoritesEnrichment}
                        disabled={enrichmentStarting || enrichmentPreview === 0 || (enrichmentStatus?.status === 'Queued' || enrichmentStatus?.status === 'Processing')}
                        className="shrink-0 flex items-center gap-1 px-3 py-2 bg-violet-600 hover:bg-violet-500 disabled:bg-gray-700 disabled:text-gray-500 text-white rounded-lg text-sm font-bold transition"
                    >
                        {(enrichmentStarting || enrichmentStatus?.status === 'Queued' || enrichmentStatus?.status === 'Processing') && <LoaderCircle size={15} className="animate-spin" />}
                        Enrich favorites
                    </button>
                    {enrichmentStatus?.status === 'Completed' && enrichmentStatus.failed > 0 && (
                        <button
                            onClick={handleRetryFailures}
                            disabled={enrichmentStarting}
                            className="shrink-0 px-3 py-2 bg-gray-700 hover:bg-violet-500 disabled:text-gray-500 text-white rounded-lg text-sm font-bold transition"
                        >
                            Retry failures
                        </button>
                    )}
                </div>
            </div>

            {/* Soft-Deleted Media Management / Trash Section */}
            <div className="bg-gray-800 rounded-xl p-6 border border-gray-700">
                <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 pb-4 border-b border-gray-700">
                    <div>
                        <h2 className="text-xl font-bold flex items-center gap-2">
                            <ArchiveRestore className="text-amber-400" />
                            曲库软删除管理（回收站）
                        </h2>
                        <p className="text-sm text-gray-400 mt-1">
                            被软删除的曲目不会在曲库（Library）普通路由和播放器中展示，但物理文件保持安全无损。
                        </p>
                    </div>
                    <label className="inline-flex items-center gap-3 cursor-pointer bg-gray-900/80 px-4 py-2 rounded-lg border border-gray-700 hover:border-gray-600 transition select-none">
                        <input
                            type="checkbox"
                            checked={showDeleted}
                            onChange={(e) => handleToggleShowDeleted(e.target.checked)}
                            className="sr-only peer"
                        />
                        <div className="relative w-11 h-6 bg-gray-700 peer-focus:outline-none rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:start-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-amber-500"></div>
                        <span className="text-sm font-medium text-gray-200">
                            {showDeleted ? '显示软删除列表' : '隐藏软删除列表'}
                        </span>
                    </label>
                </div>

                {showDeleted && (
                    <div className="mt-6 space-y-4">
                        {/* Search & Actions Bar */}
                        <div className="flex flex-col sm:flex-row items-stretch sm:items-center justify-between gap-3">
                            <form onSubmit={handleSearchSubmit} className="relative flex-1 max-w-md">
                                <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 pointer-events-none" />
                                <input
                                    type="text"
                                    value={searchInput}
                                    onChange={(e) => handleSearchInputChange(e.target.value)}
                                    placeholder="搜索标题、歌手、专辑或物理路径..."
                                    className="w-full bg-gray-900/90 border border-gray-700 focus:border-amber-500/80 focus:ring-1 focus:ring-amber-500/80 rounded-lg pl-9 pr-8 py-2 text-sm text-gray-100 placeholder-gray-500 transition outline-none"
                                />
                                {searchInput && (
                                    <button
                                        type="button"
                                        onClick={handleClearSearch}
                                        className="absolute right-2.5 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-200 p-0.5 rounded transition"
                                        title="清空搜索"
                                    >
                                        <X size={14} />
                                    </button>
                                )}
                            </form>

                            <div className="flex items-center gap-2 self-end sm:self-auto">
                                {selectedIds.size > 0 && (
                                    <button
                                        onClick={handleBatchRestore}
                                        disabled={batchRestoring}
                                        className="flex items-center gap-1.5 px-3 py-1.5 bg-emerald-600 hover:bg-emerald-500 disabled:bg-gray-700 disabled:text-gray-500 text-white rounded-lg text-xs font-bold transition shadow-sm"
                                    >
                                        {batchRestoring ? <LoaderCircle size={13} className="animate-spin" /> : <Undo2 size={13} />}
                                        批量恢复 ({selectedIds.size})
                                    </button>
                                )}
                                <div className="text-xs text-gray-400 px-2 py-1 bg-gray-900/50 rounded border border-gray-800">
                                    {deletedSearch ? (
                                        <>匹配: <span className="text-amber-400 font-bold">{deletedTotal.toLocaleString()}</span> 首</>
                                    ) : (
                                        <>已删除: <span className="text-amber-400 font-bold">{deletedTotal.toLocaleString()}</span> 首</>
                                    )}
                                </div>
                                <button
                                    onClick={() => loadDeleted()}
                                    disabled={loadingDeleted}
                                    className="flex items-center gap-1 text-xs text-gray-300 hover:text-white px-2.5 py-1.5 rounded-lg bg-gray-700/60 hover:bg-gray-700 transition border border-gray-600/40"
                                    title="刷新软删除曲目列表"
                                >
                                    <RotateCcw size={13} className={loadingDeleted ? 'animate-spin text-amber-400' : ''} />
                                    刷新
                                </button>
                            </div>
                        </div>

                        {loadingDeleted ? (
                            <div className="flex flex-col items-center justify-center py-16 text-gray-400 text-sm bg-gray-900/20 rounded-xl border border-gray-800">
                                <LoaderCircle size={26} className="animate-spin text-amber-400 mb-2" />
                                正在加载软删除曲目...
                            </div>
                        ) : deletedSongs.length === 0 ? (
                            <div className="text-center py-12 text-gray-400 text-sm bg-gray-900/40 rounded-xl border border-gray-800 px-4">
                                {deletedSearch ? (
                                    <div className="space-y-2">
                                        <p className="text-gray-300">未找到与“<span className="text-amber-400 font-semibold">{deletedSearch}</span>”匹配的软删除曲目</p>
                                        <button
                                            onClick={handleClearSearch}
                                            className="text-xs text-amber-400 hover:text-amber-300 underline"
                                        >
                                            清空搜索条件
                                        </button>
                                    </div>
                                ) : (
                                    <p className="text-gray-500">回收站暂无软删除曲目，所有库内歌曲均为正常有效状态。</p>
                                )}
                            </div>
                        ) : (
                            <div className="space-y-4">
                                <div className="overflow-x-auto rounded-xl border border-gray-700/80 bg-gray-900/50 shadow-inner">
                                    <table className="w-full text-left text-sm">
                                        <thead className="bg-gray-900/95 text-gray-400 text-xs uppercase tracking-wider sticky top-0 border-b border-gray-700/80">
                                            <tr>
                                                <th className="px-3 py-3 w-10 text-center">
                                                    <button
                                                        type="button"
                                                        onClick={toggleSelectAll}
                                                        className="text-gray-400 hover:text-white transition flex items-center justify-center"
                                                        title={allOnPageSelected ? '取消全选本页' : '全选本页'}
                                                    >
                                                        {allOnPageSelected ? (
                                                            <CheckSquare size={16} className="text-amber-400" />
                                                        ) : (
                                                            <Square size={16} />
                                                        )}
                                                    </button>
                                                </th>
                                                <th className="px-4 py-3">标题与路径</th>
                                                <th className="px-4 py-3">歌手</th>
                                                <th className="px-4 py-3">专辑</th>
                                                <th className="px-4 py-3">删除时间</th>
                                                <th className="px-4 py-3 text-right">操作</th>
                                            </tr>
                                        </thead>
                                        <tbody className="divide-y divide-gray-700/40">
                                            {deletedSongs.map((song) => {
                                                const isSelected = selectedIds.has(song.id);
                                                const isRestoring = restoringId === song.id;
                                                return (
                                                    <tr
                                                        key={song.id}
                                                        className={`hover:bg-gray-700/40 transition ${isSelected ? 'bg-amber-500/10' : ''}`}
                                                    >
                                                        <td className="px-3 py-3 text-center">
                                                            <button
                                                                type="button"
                                                                onClick={() => toggleSelectSong(song.id)}
                                                                className="text-gray-400 hover:text-white transition flex items-center justify-center"
                                                            >
                                                                {isSelected ? (
                                                                    <CheckSquare size={16} className="text-amber-400" />
                                                                ) : (
                                                                    <Square size={16} />
                                                                )}
                                                            </button>
                                                        </td>
                                                        <td className="px-4 py-3">
                                                            <div className="font-medium text-gray-100">{song.title || '无标题'}</div>
                                                            <div className="text-xs text-gray-500 font-mono truncate max-w-md" title={song.filePath}>
                                                                {song.filePath}
                                                            </div>
                                                        </td>
                                                        <td className="px-4 py-3 text-gray-300">{song.artist || '—'}</td>
                                                        <td className="px-4 py-3 text-gray-300">{song.album || '—'}</td>
                                                        <td className="px-4 py-3 text-gray-400 text-xs whitespace-nowrap">
                                                            {song.deletedAt ? new Date(song.deletedAt).toLocaleString() : '—'}
                                                        </td>
                                                        <td className="px-4 py-3 text-right">
                                                            <button
                                                                onClick={() => handleRestoreSong(song.id, song.title)}
                                                                disabled={isRestoring || batchRestoring}
                                                                className="inline-flex items-center gap-1 px-3 py-1 bg-emerald-600/80 hover:bg-emerald-600 disabled:bg-gray-700 disabled:text-gray-500 text-white rounded text-xs font-semibold transition"
                                                                title="恢复到曲库并在前端重新显示"
                                                            >
                                                                <RotateCcw size={12} className={isRestoring ? 'animate-spin' : ''} />
                                                                {isRestoring ? '恢复中...' : '恢复'}
                                                            </button>
                                                        </td>
                                                    </tr>
                                                );
                                            })}
                                        </tbody>
                                    </table>
                                </div>

                                {/* Pagination */}
                                <Pagination
                                    currentPage={deletedPage}
                                    totalItems={deletedTotal}
                                    pageSize={deletedPageSize}
                                    onPageChange={handlePageChange}
                                    onPageSizeChange={handlePageSizeChange}
                                />
                            </div>
                        )}
                    </div>
                )}
            </div>
        </div>
    );
}
