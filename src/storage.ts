import { seed, type Change, type Data } from './model'

export function openStorage(name = 'pomogator'): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(name, 1)
    request.onupgradeneeded = () => request.result.createObjectStore('organizer').put(seed(), 'data')
    request.onerror = () => reject(request.error)
    request.onblocked = () => reject(new Error('Закройте другие вкладки ПОМОГАТОР и обновите страницу.'))
    request.onsuccess = () => {
      const database = request.result
      database.onversionchange = () => database.close()
      resolve(database)
    }
  })
}

export function transact(database: IDBDatabase, change?: Change): Promise<Data> {
  return new Promise((resolve, reject) => {
    const transaction = database.transaction('organizer', change ? 'readwrite' : 'readonly')
    const store = transaction.objectStore('organizer')
    const request = store.get('data')
    let result: Data
    let failure: unknown
    request.onsuccess = () => {
      try {
        if (!request.result) throw new Error('Локальная база недоступна.')
        result = change ? change(request.result) : request.result
        if (change) store.put(result, 'data')
      } catch (error) {
        failure = error
        transaction.abort()
      }
    }
    transaction.oncomplete = () => resolve(result)
    transaction.onabort = () => reject(failure ?? transaction.error ?? new Error('Операция отменена.'))
  })
}
