import { useState } from 'react';
import { Download, Upload, FileText, CheckCircle, AlertTriangle } from 'lucide-react';
import { exportFavorites, importFavorites } from '../services/api';

export default function BackupPage() {
    const [file, setFile] = useState<File | null>(null);
    const [textInput, setTextInput] = useState('');
    const [status, setStatus] = useState<{ type: 'success' | 'error', msg: string } | null>(null);
    const [loading, setLoading] = useState(false);

    const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files && e.target.files[0]) {
            setFile(e.target.files[0]);
            setTextInput('');
            setStatus(null);
        }
    };

    const handleExport = async () => {
        try {
            const res = await exportFavorites();
            const url = window.URL.createObjectURL(new Blob([res.data]));
            const link = document.createElement('a');
            link.href = url;
            link.setAttribute('download', `webmusic_favorites_${new Date().toISOString().slice(0, 10)}.txt`);
            document.body.appendChild(link);
            link.click();
            link.remove();
        } catch (e) {
            console.error("Export failed", e);
            setStatus({ type: 'error', msg: 'Export failed. Please try again.' });
        }
    };

    const handleImport = async () => {
        setLoading(true);
        setStatus(null);

        try {
            let uploadFile = file;

            if (!uploadFile && textInput.trim()) {
                const blob = new Blob([textInput], { type: 'text/plain' });
                uploadFile = new File([blob], "favorites_import.txt", { type: "text/plain" });
            }

            if (!uploadFile) {
                setStatus({ type: 'error', msg: 'Please select a file or paste text content.' });
                setLoading(false);
                return;
            }

            const formData = new FormData();
            formData.append('file', uploadFile);

            const res = await importFavorites(formData);
            setStatus({ type: 'success', msg: `Successfully imported ${res.data.imported} songs!` });

            setFile(null);
            setTextInput('');
        } catch (error) {
            console.error(error);
            setStatus({ type: 'error', msg: 'Import failed. Check format and try again.' });
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="p-4 md:p-8 max-w-2xl mx-auto pb-32">
            <header className="mb-8">
                <h1 className="text-3xl font-bold text-white mb-2">Backup & Restore</h1>
                <p className="text-gray-400">Manage your data migration. Currently supports Favorites.</p>
            </header>

            <div className="space-y-8">
                {/* Export Section */}
                <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6">
                    <div className="flex items-start gap-4">
                        <div className="p-3 bg-blue-500/20 rounded-xl text-blue-400">
                            <Download size={24} />
                        </div>
                        <div className="flex-1">
                            <h3 className="text-xl font-bold text-gray-200 mb-1">Export Favorites</h3>
                            <p className="text-gray-500 text-sm mb-4">
                                Download a text file list of all your favorite songs (file paths).
                            </p>
                            <button
                                onClick={handleExport}
                                className="px-4 py-2 bg-blue-600 hover:bg-blue-500 text-white rounded-lg font-medium transition shadow-lg shadow-blue-500/20 flex items-center gap-2"
                            >
                                <Download size={18} />
                                Download .txt
                            </button>
                        </div>
                    </div>
                </div>

                {/* Import Section */}
                <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6">
                    <div className="flex items-start gap-4 mb-6">
                        <div className="p-3 bg-green-500/20 rounded-xl text-green-400">
                            <Upload size={24} />
                        </div>
                        <div>
                            <h3 className="text-xl font-bold text-gray-200 mb-1">Import Favorites</h3>
                            <p className="text-gray-500 text-sm">
                                Restore favorites from a text file or pasted list. One path per line.
                            </p>
                        </div>
                    </div>

                    <div className="space-y-4 pl-0 md:pl-[60px]">
                        {/* File Upload */}
                        <div className="border-2 border-dashed border-gray-700 hover:border-gray-500 rounded-xl p-4 text-center transition bg-black/20">
                            <input
                                type="file"
                                accept=".txt"
                                onChange={handleFileChange}
                                className="hidden"
                                id="backup-file-upload"
                            />
                            <label htmlFor="backup-file-upload" className="cursor-pointer block">
                                <FileText size={24} className="mx-auto text-gray-500 mb-2" />
                                <p className="text-sm text-gray-400">
                                    {file ? (
                                        <span className="text-blue-400 font-medium">{file.name}</span>
                                    ) : (
                                        "Click to upload .txt file"
                                    )}
                                </p>
                            </label>
                        </div>

                        <div className="relative text-center">
                            <span className="text-xs uppercase text-gray-600 font-bold bg-gray-900 px-2 z-10 relative">OR Paste Text</span>
                            <div className="absolute inset-x-0 top-1/2 border-t border-gray-800 -z-0"></div>
                        </div>

                        <textarea
                            value={textInput}
                            onChange={(e) => {
                                setTextInput(e.target.value);
                                setFile(null);
                                setStatus(null);
                            }}
                            placeholder="/music/Jay Chou/Fantasy/01.mp3"
                            className="w-full h-32 bg-black/30 border border-gray-700 rounded-xl p-3 text-sm text-gray-300 focus:outline-none focus:border-green-500 resize-none font-mono"
                        />

                        {status && (
                            <div className={`p-3 rounded-lg flex items-center gap-3 text-sm ${status.type === 'success' ? 'bg-green-500/10 text-green-400' : 'bg-red-500/10 text-red-400'}`}>
                                {status.type === 'success' ? <CheckCircle size={18} /> : <AlertTriangle size={18} />}
                                {status.msg}
                            </div>
                        )}

                        <button
                            onClick={handleImport}
                            disabled={loading || (!file && !textInput.trim())}
                            className="w-full py-3 bg-green-600 hover:bg-green-500 disabled:bg-gray-800 disabled:text-gray-500 text-white rounded-xl font-medium transition shadow-lg shadow-green-500/20 flex items-center justify-center gap-2"
                        >
                            {loading ? (
                                <span>Processing...</span>
                            ) : (
                                <>
                                    <Upload size={18} />
                                    Start Import
                                </>
                            )}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}
